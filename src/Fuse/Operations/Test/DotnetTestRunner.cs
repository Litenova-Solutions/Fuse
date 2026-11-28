using System.Diagnostics;
using System.Globalization;
using System.Text;
using Fuse.Dotnet.Build;
using Fuse.Dotnet.Processes;
using Fuse.Dotnet.Test;
using Fuse.Operations.Build;
using Fuse.Operations.Test.Rendering;
using Fuse.Paths;
using Fuse.Protocol.Testing;
namespace Fuse.Operations.Test;

/// <summary>
///     Runs <c>dotnet test</c> and Microsoft.Testing.Platform test applications: the <c>dotnet test</c>
///     fallback for the caller's arguments, direct platform runs, TRX reading, early stop and argument shaping.
/// </summary>
internal static class DotnetTestRunner
{
    /// <summary>How long a run waits for a test process it stopped early to exit before answering without it.</summary>
    private const int EarlyStopExitSeconds = 5;

    /// <summary>
    ///     Runs one project on Microsoft.Testing.Platform, with its framework's filter. With the repository's
    ///     <c>global.json</c> opt-in, <c>dotnet test</c> runs it. Without one, <c>dotnet test</c> cannot reach it: the
    ///     project has no VSTest target to run (it exits 0 having run nothing, or its targets refuse), and
    ///     <c>--project</c> is not a switch of that command. The project then builds and its test application runs
    ///     directly from what the build wrote, once per target framework, with the output folder as its working
    ///     directory, as a test host runs it. A filtered run that ran no test is answered as the platform's own
    ///     no-test run, never as a pass.
    /// </summary>
    internal static async Task<TestRunOutcome> RunPlatformAsync(
        RepoRoot root, TestRun run, bool runner, TestScopeArguments? scope, CancellationToken cancellationToken, int maxFailures = TestOperation.DefaultMaxFailures, TimeSpan? inactivityTimeout = null)
    {
        string[] switches = scope?.BuildSwitches ?? [];
        if (runner || run.TargetPaths.Length == 0)
            return await ThroughDotnetTestAsync(root, run, runner, switches, cancellationToken, maxFailures, inactivityTimeout).ConfigureAwait(false);

        var results = Path.Combine(root.StateDirectory, "results", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // --no-build runs what an earlier build wrote, as dotnet test --no-build does.
            var (built, log, _) = switches.Contains("--no-build")
                ? (new ProcessResult(0, ""), "", new List<string>())
                : await BuildOperation.RunLoggedAsync(["build", run.Project, "--no-restore", "-nologo", "-tl:off", .. switches.Where(s => s != "--no-build")], root.Path, results, cancellationToken).ConfigureAwait(false);
            if (built.ExitCode != 0)
                return new TestRunOutcome(null, built, log, results, null);
            var targets = run.TargetPaths
                .Select(t => scope?.Configuration is { } configuration ? ShadowTestRunner.ForConfiguration(t, configuration) : t)
                .Where(t => scope?.Framework is not { } framework || ShadowTestRunner.HasSegment(t, framework))
                .Where(File.Exists)
                .ToList();
            // A .NET Framework target builds an assembly with no runtimeconfig.json, which dotnet exec cannot start
            // (hostpolicy.dll is not found), so a project with one runs through dotnet test, which starts it the right way.
            if (targets.Count == 0 || targets.Any(t => t.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.ChangeExtension(t, ".runtimeconfig.json"))))
            {
                // dotnet test starts such a target itself, and says in its own words when the build wrote none. The build
                // above is done, so dotnet test does not build the project again.
                return await ThroughDotnetTestAsync(root, run, runner, Built(switches), cancellationToken, maxFailures, inactivityTimeout).ConfigureAwait(false);
            }

            string[] settings = run.Filter is not null || run.RunSettings is not null
                ? ["--settings", RunSettingsFile.Write(results, run.Filter, run.RunSettings)]
                : [];
            var aggregate = TestRunSummary.Empty;
            ProcessResult? last = null;
            foreach (var target in targets)
            {
                var arguments = new List<string>();
                string file;
                if (target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    file = "dotnet";
                    arguments.Add("exec");
                    arguments.Add(target);
                }
                else
                {
                    file = target;
                }

                arguments.AddRange(run.FilterArguments);
                arguments.AddRange(settings);
                var directory = Path.GetDirectoryName(target);
                ArgumentNullException.ThrowIfNull(directory);
                var (process, earlyFailures) = await RunWithEarlyStopAsync(file, [.. arguments], directory, maxFailures, cancellationToken, TestOperation.EffectiveTimeout(inactivityTimeout)).ConfigureAwait(false);
                // A target that hung aborts the run: the same tests hang on every target framework, so running on
                // would only wait out another timeout. It is reported as timed out, never as passed.
                if (process.TimedOut)
                    return new TestRunOutcome(null, process, log, results, ShadowTestRunner.TargetFrameworkOfPath(target));
                var outcome = TestingPlatformOutput.Read(process.Output, root.Path) ?? XUnitOutput.Read(process.Output, root.Path);
                if (outcome is null && earlyFailures.Count > 0)
                    outcome = FailureGroups.SynthesizeFailures(earlyFailures);
                // Counts that say nothing failed from a run that exited with a code other than 0 do not explain its exit code.
                if (outcome is { Failed: 0 } && process.ExitCode != 0)
                    outcome = null;
                // A target that exits 0 and prints no results is not a test application that runs itself (an NUnit or
                // MSTest project can reference the platform without being one), so the project runs through dotnet test,
                // which finds its tests, instead of passing with none.
                if (outcome is null && process.ExitCode == 0)
                    return await ThroughDotnetTestAsync(root, run, runner, Built(switches), cancellationToken, maxFailures, inactivityTimeout).ConfigureAwait(false);
                if (outcome is null)
                    return new TestRunOutcome(null, process, log, results, null);
                aggregate = aggregate.Add(outcome);
                last = process;
            }

            await ShadowTestRunner.CollectTestMapsAsync(root, run, [results, .. targets.Select(t => Path.GetDirectoryName(t)).OfType<string>()], cancellationToken).ConfigureAwait(false);
            if (aggregate.Total == 0 && (run.Filter is not null || run.FilterArguments.Length > 0))
                return new TestRunOutcome(null, new ProcessResult(TestResultRenderer.NoTestRanExitCode, last?.Output ?? ""), log, results, null);
            return new TestRunOutcome(aggregate, null, log, results, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(results))
                    Directory.Delete(results, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    ///     Runs a platform project through <c>dotnet test</c> instead of starting its applications. With the platform
    ///     runner in <c>global.json</c>, <c>dotnet test</c> takes the project by <c>--project</c> and the framework's
    ///     filter options. Without it, <c>dotnet test</c> is VSTest's: it takes the project positionally, refuses
    ///     <c>--project</c>, reports to TRX, and reads a filter from the run settings, so a framework's own filter options
    ///     are left out and xUnit v3 and TUnit run whole; MSTest and NUnit still read their VSTest filter there.
    /// </summary>
    internal static async Task<TestRunOutcome> ThroughDotnetTestAsync(
        RepoRoot root, TestRun run, bool runner, string[] switches, CancellationToken cancellationToken, int maxFailures = TestOperation.DefaultMaxFailures, TimeSpan? inactivityTimeout = null)
    {
        string[] maxFailArgs = [];
        if (runner && maxFailures > 0
            && await BuildOperation.IsNet11OrLaterAsync(root.Path, cancellationToken).ConfigureAwait(false)
            && !switches.Any(s => s.StartsWith("--maximum-failed-tests", StringComparison.OrdinalIgnoreCase))
            && !run.FilterArguments.Any(s => s.StartsWith("--maximum-failed-tests", StringComparison.OrdinalIgnoreCase)))
        {
            maxFailArgs = ["--maximum-failed-tests", maxFailures.ToString(CultureInfo.InvariantCulture)];
        }

        var ran = runner
            ? await RunDotnetTestAsync(root, root.Path, ["--project", run.Project, "--no-restore", .. switches, .. maxFailArgs, .. run.FilterArguments], cancellationToken, testingPlatform: true, run: run, maxFailures: maxFailures, inactivityTimeout: inactivityTimeout).ConfigureAwait(false)
            : await RunDotnetTestAsync(root, root.Path, [run.Project, "--no-restore", .. switches], cancellationToken, run: run, maxFailures: maxFailures, inactivityTimeout: inactivityTimeout).ConfigureAwait(false);
        return FilteredNoTest(run, ran);
    }

    /// <summary>A filtered platform run that ran no test, answered as the platform's own no-test run; any other run unchanged.</summary>
    internal static TestRunOutcome FilteredNoTest(
        TestRun run, TestRunOutcome ran) =>
        ran.Outcome is { Total: 0 } && (run.Filter is not null || run.FilterArguments.Length > 0)
            ? new TestRunOutcome(null, new ProcessResult(TestResultRenderer.NoTestRanExitCode, ""), ran.Log, ran.ResultsDirectory, null)
            : ran;

    /// <summary>
    ///     Runs <c>dotnet test</c> and reads its results: from its TRX files, or for a Microsoft.Testing.Platform run from
    ///     the summary and failures it prints. Without results it returns the process, which is null for a
    ///     Microsoft.Testing.Platform run that exited with 0.
    /// </summary>
    /// <param name="assembly">True when <paramref name="arguments"/> name a test assembly: <c>dotnet test</c> then hands them to VSTest, which rejects MSBuild switches.</param>
    /// <param name="run">The planned VSTest run, whose filter and project runsettings go into a runsettings file; null for none.</param>
    /// <param name="maxFailures">Maximum failures before early-stopping VSTest; 0 disables early stop.</param>
    /// <param name="inactivityTimeout">How long the test process may write no output before its tree is killed and the result reads timed out, or null for the timeout every run uses.</param>
    /// <returns>The results, or the process without them, and the build's file log (<see cref="BuildLog"/>) for a VSTest run that builds with MSBuild, or empty.</returns>
    internal static async Task<TestRunOutcome> RunDotnetTestAsync(
        RepoRoot root, string workingDirectory, string[] arguments, CancellationToken cancellationToken, bool testingPlatform = false, bool assembly = false, TestRun? run = null, int maxFailures = TestOperation.DefaultMaxFailures, TimeSpan? inactivityTimeout = null)
    {
        var results = Path.Combine(root.StateDirectory, "results", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string[] reporting = testingPlatform
                ? []
                : assembly
                    ? ["--logger", "trx;LogFilePrefix=fuse", "--results-directory", results]
                    : ["--logger", "trx;LogFilePrefix=fuse", "--results-directory", results, "-nologo", "-tl:off"];
            string[] command = ["test", .. arguments, .. Settings(run, results, assembly), .. reporting];
            // A VSTest run on a project builds it with MSBuild first, so its file log can say what a failed command printed.
            // Arguments that name an assembly go to VSTest, which rejects MSBuild's switches.
            var builds = !testingPlatform && !assembly && !arguments.Any(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            ProcessResult result;
            string log;
            List<string> earlyFailures;
            if (builds)
            {
                // A VSTest run that builds with MSBuild honors the failure cap the same way: the build's own
                // lines never name a test failure, and the file log still says what a failed command printed.
                (result, log, earlyFailures) = await RunLoggedTestAsync(command, workingDirectory, results, cancellationToken, maxFailures, inactivityTimeout).ConfigureAwait(false);
            }
            else
            {
                (result, earlyFailures) = await RunWithEarlyStopAsync("dotnet", command, workingDirectory, maxFailures, cancellationToken, TestOperation.EffectiveTimeout(inactivityTimeout)).ConfigureAwait(false);
                log = "";
            }

            if (result.TimedOut)
                return new TestRunOutcome(null, result, log, results, run is null ? TestScopeArguments.FrameworkFromArguments(arguments) : ShadowTestRunner.TargetFrameworkOf(run));
            var outcome = TrxReader.ReadDirectory(results, root.Path) ?? (testingPlatform ? TestingPlatformOutput.Read(result.Output, root.Path) : null);
            if (outcome is null && earlyFailures.Count > 0)
                outcome = FailureGroups.SynthesizeFailures(earlyFailures);
            if (outcome is not null)
                await ShadowTestRunner.CollectTestMapsAsync(root, run, [results, .. (run?.TargetPaths ?? []).Select(t => Path.GetDirectoryName(t)).OfType<string>()], cancellationToken).ConfigureAwait(false);
            // Counts that say nothing failed from a run that exited with a code other than 0 do not explain its exit code.
            if (testingPlatform && outcome is { Failed: 0 } && result.ExitCode != 0)
                outcome = null;
            if (outcome is null && testingPlatform)
                return new TestRunOutcome(null, result.ExitCode == 0 ? null : result, log, results, null);
            if (outcome is null)
                return new TestRunOutcome(null, result, log, results, null);
            return new TestRunOutcome(outcome, null, log, results, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(results))
                    Directory.Delete(results, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    ///     Runs <c>dotnet test</c> with <paramref name="arguments"/> and the file log, killing the process when it
    ///     writes no output for the test inactivity timeout. It mirrors the logged build run, because a test run that
    ///     builds with MSBuild is still a test process and must end too.
    /// </summary>
    /// <param name="logDirectory">Where the log is written; it is deleted once read.</param>
    /// <param name="maxFailures">Maximum test failures before stopping early; 0 runs to the end.</param>
    internal static async Task<(ProcessResult Result, string Log, List<string> EarlyFailures)> RunLoggedTestAsync(string[] arguments, string workingDirectory, string logDirectory, CancellationToken cancellationToken, int maxFailures = 0, TimeSpan? inactivityTimeout = null)
    {
        var path = Path.Combine(logDirectory, $"build-{Guid.NewGuid():N}.log");
        try
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Without a place for the log, the run goes without one.
                var (unlogged, unloggedFailures) = await RunWithEarlyStopAsync("dotnet", arguments, workingDirectory, maxFailures, cancellationToken, TestOperation.EffectiveTimeout(inactivityTimeout)).ConfigureAwait(false);
                return (unlogged, "", unloggedFailures);
            }

            var (result, failures) = await RunWithEarlyStopAsync("dotnet", [.. arguments, .. BuildLog.Arguments(path)], workingDirectory, maxFailures, cancellationToken, TestOperation.EffectiveTimeout(inactivityTimeout)).ConfigureAwait(false);
            return (result, ReadLog(path), failures);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The build's file log, or empty when MSBuild wrote none, as for a command line it refused.</summary>
    internal static string ReadLog(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>
    ///     Runs a test process while watching stdout for failure lines, terminating the process tree early once
    ///     <paramref name="maxFailures"/> distinct failures are observed (after a short grace window for details to
    ///     flush). With no cap it runs the process to the end, as <see cref="ProcessRunner"/> does.
    /// </summary>
    /// <param name="inactivityTimeout">
    ///     How long the process may write no output before its tree is killed and the result reads timed out, or null
    ///     to wait without a timeout. Every output line renews it.
    /// </param>
    internal static async Task<(ProcessResult Result, List<string> Failures)> RunWithEarlyStopAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int maxFailures,
        CancellationToken cancellationToken,
        TimeSpan? inactivityTimeout = null)
    {
        if (maxFailures <= 0)
            return (await ProcessRunner.RunAsync(fileName, arguments, workingDirectory, cancellationToken, inactivityTimeout).ConfigureAwait(false), []);

        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        var output = new StringBuilder();
        var failures = new List<string>();
        // When the last output line arrived, in Environment.TickCount64 milliseconds; every line renews the timeout.
        var lastOutput = Environment.TickCount64;
        using var earlyStopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var killScheduled = 0;

        using var process = new Process { StartInfo = psi };

        void OnLine(string? line)
        {
            if (line is null)
                return;
            lock (output)
            {
                Volatile.Write(ref lastOutput, Environment.TickCount64);
                output.Append(line).Append('\n');
                if (FailureGroups.TryParseFailureName(line) is { } failedName && !failures.Contains(failedName, StringComparer.Ordinal))
                {
                    failures.Add(failedName);
                    if (failures.Count >= maxFailures && Interlocked.Exchange(ref killScheduled, 1) == 0)
                        earlyStopCts.CancelAfter(TimeSpan.FromMilliseconds(250));
                }
            }
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        try
        {
            if (!process.Start())
                return (new ProcessResult(-1, $"could not start {fileName}"), []);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (new ProcessResult(-1, $"could not start {fileName}: {e.Message}"), []);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var earlyStopped = false;
        var timedOut = false;
        while (true)
        {
            if (inactivityTimeout is null)
            {
                try
                {
                    await process.WaitForExitAsync(earlyStopCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    earlyStopped = true;
                }
                catch (OperationCanceledException)
                {
                    ProcessRunner.KillProcessTree(process);
                    throw;
                }

                break;
            }

            var remaining = inactivityTimeout.Value - TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref lastOutput));
            if (remaining <= TimeSpan.Zero)
            {
                // The process may have exited between its last line and now; only one that is still running is
                // killed and reported as timed out.
                if (process.HasExited)
                    break;
                ProcessRunner.KillProcessTree(process);
                timedOut = true;
                break;
            }

            using var slice = CancellationTokenSource.CreateLinkedTokenSource(earlyStopCts.Token);
            slice.CancelAfter(remaining);
            try
            {
                await process.WaitForExitAsync(slice.Token).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ProcessRunner.KillProcessTree(process);
                    throw;
                }

                if (earlyStopCts.IsCancellationRequested)
                {
                    earlyStopped = true;
                    break;
                }
                // The slice fired; output may have arrived since, so the loop recomputes instead of killing.
            }
        }

        if (earlyStopped)
        {
            ProcessRunner.KillProcessTree(process);
            try
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(TimeSpan.FromSeconds(EarlyStopExitSeconds));
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        if (timedOut)
        {
            try
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                grace.CancelAfter(TimeSpan.FromSeconds(EarlyStopExitSeconds));
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            catch (OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        lock (output)
            return (new ProcessResult(process.HasExited ? process.ExitCode : -1, output.ToString()) { TimedOut = timedOut }, [.. failures]);
    }

    /// <summary>
    ///     The <c>--settings</c> argument of a run whose filter is a VSTest expression (a VSTest run, or MSTest and NUnit
    ///     on Microsoft.Testing.Platform): a runsettings file in its results folder that carries its filter,
    ///     so a filter of any length leaves the command line short. A shadow run gets one for the project's runsettings
    ///     too, because <c>dotnet test</c> on an assembly does not read the project; a build with MSBuild without a filter
    ///     reads them itself.
    /// </summary>
    internal static string[] Settings(TestRun? run, string results, bool assembly)
    {
        if (run is null || (run.Filter is null && !(assembly && run.RunSettings is not null)))
            return [];
        return ["--settings", RunSettingsFile.Write(results, run.Filter, run.RunSettings)];
    }

    /// <summary>The build switches with <c>--no-build</c>, for a <c>dotnet test</c> after the project was built.</summary>
    internal static string[] Built(string[] switches) => switches.Contains("--no-build") ? switches : [.. switches, "--no-build"];

}
