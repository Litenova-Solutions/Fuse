using System.Text;
using Fuse.Dotnet;
using Fuse.Engine.Client;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>
///     Runs tests and prints failures. With no arguments it runs the tests affected by the working-tree changes
///     (planned by the engine); with arguments it passes them to <c>dotnet test</c> and adds result reporting.
/// </summary>
internal static class TestOperation
{
    private const int MaxFailuresShown = 10;
    private const int MaxNamesShown = 100;

    /// <param name="session">
    ///     The session the run is for, as its harness identifies it, or null. With one, the affected tests leave out the
    ///     changes only other sessions wrote. The pre-shell hook passes it to <c>fuse test</c>.
    /// </param>
    public static async Task<OperationResult> RunAsync(RepoRoot root, string workingDirectory, IReadOnlyList<string> arguments, bool all, CancellationToken cancellationToken, string? session = null)
    {
        var started = Environment.TickCount64;
        if (arguments.Count > 0)
        {
            // dotnet test with the caller's arguments builds with MSBuild, so it holds the lock until it exits.
            using var argumentsLock = BuildLock.AcquireForClient(root);
            // On Microsoft.Testing.Platform the arguments go to the test application, which rejects VSTest's logger options.
            var testingPlatform = GlobalJson.UsesTestingPlatform(workingDirectory, root.Path);
            var (outcome, process, log, resultsDirectory) = await RunDotnetTestAsync(root, workingDirectory, NormalizeArguments(workingDirectory, arguments), cancellationToken, testingPlatform).ConfigureAwait(false);
            const string summary = "ran the tests your dotnet test arguments name";
            return outcome is null
                ? WithoutResults(process, root, ArgumentsRun, summary, Seconds(started), log, resultsDirectory)
                : Render(outcome, summary, Seconds(started), ResultsFile.WriteTests(root, [("", outcome)]));
        }

        // Held from before the plan request: preparing the plan makes the engine mirror and emit into the copies the runs
        // start from, which no other build or test in this repository may do at the same time. RunPlanAsync gives it up
        // once its runs hold their copies.
        var buildLock = BuildLock.AcquireForClient(root);
        try
        {
            EngineRequest request = all ? new EngineRequest.PlanAllTests() : new EngineRequest.PlanAffectedTests { Session = session };
            var response = await EngineClient.SendAsync(root, request, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            if (response is not EngineResponse.PlanAnswered answered)
                return OperationResult.Unanswered(response, "test plan");
            var plan = answered.Plan;
            if (plan.Runs.Length == 0)
                return new OperationResult(Outcome.Clean, $"fuse: {plan.Summary}");
            var held = buildLock;
            buildLock = null;
            return await RunPlanAsync(root, plan, started, cancellationToken, held).ConfigureAwait(false);
        }
        finally
        {
            buildLock?.Dispose();
        }
    }

    /// <summary>Runs every run of <paramref name="plan"/> and answers for all of them together.</summary>
    /// <param name="root">The repository the plan is for.</param>
    /// <param name="plan">The engine's plan, with at least one run.</param>
    /// <param name="started">When the operation started, in <see cref="Environment.TickCount64"/> milliseconds, for the times the answer prints.</param>
    /// <param name="cancellationToken">Kills the running <c>dotnet test</c> processes.</param>
    /// <param name="held">
    ///     The build lock the caller took before it asked for the plan, which this call owns and releases, or null when
    ///     the caller holds none.
    /// </param>
    /// <remarks>
    ///     Shadow runs write no build output: each holds the copy it runs from (<see cref="LocalState.HoldTestCopy"/>),
    ///     and the build lock is released while they run, so another client's build or test plan does not wait for them.
    ///     MSBuild runs share obj and bin folders across projects, so they run one after another, holding the lock.
    /// </remarks>
    internal static async Task<OperationResult> RunPlanAsync(RepoRoot root, TestPlan plan, long started, CancellationToken cancellationToken, BuildLock? held = null)
    {
        var buildLock = held;
        var copies = new List<IDisposable>();
        try
        {
            return await RunPlanHoldingAsync(root, plan, started, copies, () =>
            {
                buildLock?.Dispose();
                buildLock = null;
            }, () => buildLock ??= BuildLock.AcquireForClient(root), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var copy in copies)
                copy.Dispose();
            buildLock?.Dispose();
        }
    }

    private static async Task<OperationResult> RunPlanHoldingAsync(
        RepoRoot root, TestPlan plan, long started, List<IDisposable> copies, Action releaseLock, Func<BuildLock> takeLock, CancellationToken cancellationToken)
    {
        var groups = plan.Runs.GroupBy(r => root.PathOf(r.Project)).ToList();
        var shadowGroups = groups.Where(g => g.All(r => r.Mode is TestRunMode.Shadow && !r.UsesTestingPlatform)).ToList();
        if (shadowGroups.Count > 0)
        {
            // Every copy is held before the lock goes, so no plan prepared after it writes a copy these runs start from.
            foreach (var run in shadowGroups.SelectMany(g => g))
            {
                var folder = Path.GetDirectoryName(((TestRunMode.Shadow)run.Mode).Assembly)!;
                try
                {
                    copies.Add(LocalState.HoldTestCopy(folder));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A copy that cannot be held still runs; only a later plan could change it under the run.
                }
            }

            releaseLock();
        }

        var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<RepoPath, TrxResults>();
        await Parallel.ForEachAsync(
            shadowGroups,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (group, ct) =>
            {
                // One process per target framework, in parallel. Every run in a shadow group is a shadow run.
                var results = await Task.WhenAll(group.Select(run => RunDotnetTestAsync(
                    root, root.Path, [((TestRunMode.Shadow)run.Mode).Assembly], ct, assembly: true, run: run))).ConfigureAwait(false);
                // A shadow run that could not start (a test host or adapter problem) sends the whole project through MSBuild below.
                if (results.Any(r => r.Outcome is null))
                    return;
                outcomes[group.Key] = results.Aggregate(TrxResults.Empty, (sum, r) => sum.Add(r.Outcome!));
            }).ConfigureAwait(false);

        var aggregate = TrxResults.Empty;
        var perProject = new List<(string Project, TrxResults Outcome)>();
        // A project that did not build, or whose run ended without results, is part of the answer whatever the other
        // projects did. A Microsoft.Testing.Platform run that passed without printing a run summary is kept apart: it has
        // no counts to add.
        var problems = new List<OperationResult>();
        OperationResult? passedWithoutResults = null;
        foreach (var group in groups)
        {
            if (outcomes.TryGetValue(group.Key, out var shadowOutcome))
            {
                aggregate = aggregate.Add(shadowOutcome);
                perProject.Add((group.First().Name, shadowOutcome));
                continue;
            }

            // A run with MSBuild writes build output, so it waits for the build lock.
            takeLock();
            var run = group.First();
            var (outcome, process, log, _) = run.UsesTestingPlatform
                ? await RunDotnetTestAsync(root, root.Path, ["--project", run.Project, "--no-restore"], cancellationToken, testingPlatform: true).ConfigureAwait(false)
                : await RunDotnetTestAsync(root, root.Path, [run.Project, "--no-restore"], cancellationToken, run: run).ConfigureAwait(false);
            if (outcome is not null)
            {
                aggregate = aggregate.Add(outcome);
                perProject.Add((run.Name, outcome));
                continue;
            }

            var answer = WithoutResults(process, root, $"the test run of {run.Name}", plan.Summary, Seconds(started), log);
            if (answer.Outcome == Outcome.Clean)
                passedWithoutResults ??= answer;
            else
                problems.Add(answer);
        }

        if (aggregate.Total == 0 && problems.Count == 0 && passedWithoutResults is not null)
            return passedWithoutResults;
        if (aggregate.Total == 0 && problems.Count > 0)
            return Join(problems);

        var shadowRuns = outcomes.Count;
        var mode = shadowRuns == groups.Count ? "without MSBuild" : shadowRuns > 0 ? $"without MSBuild for {shadowRuns} of {groups.Count} project(s)" : "built with MSBuild";
        var resultsPath = aggregate.Total > 0 ? ResultsFile.WriteTests(root, perProject) : null;
        var tests = Render(aggregate, $"{plan.Summary}; {mode}", Seconds(started), resultsPath);
        // The tests' answer comes first, so the last line is a failed build or run whenever there is one.
        return problems.Count == 0 ? tests : Join([tests, .. problems]);
    }

    /// <summary>Several answers as one, in order; it found problems when any of them did.</summary>
    private static OperationResult Join(List<OperationResult> answers) =>
        answers.Count == 1
            ? answers[0]
            : new OperationResult(
                answers.Any(a => a.Outcome == Outcome.ProblemsFound) ? Outcome.ProblemsFound : Outcome.Clean,
                string.Join('\n', answers.Select(a => a.Text)));

    private static double Seconds(long started) => (Environment.TickCount64 - started) / 1000.0;

    /// <summary>
    ///     Runs <c>dotnet test</c> and reads its results: from its TRX files, or for a Microsoft.Testing.Platform run from
    ///     the summary and failures it prints. Without results it returns the process, which is null for a
    ///     Microsoft.Testing.Platform run that exited with 0.
    /// </summary>
    /// <param name="assembly">True when <paramref name="arguments"/> name a test assembly: <c>dotnet test</c> then hands them to VSTest, which rejects MSBuild switches.</param>
    /// <param name="run">The planned VSTest run, whose filter and project runsettings go into a runsettings file; null for none.</param>
    /// <returns>The results, or the process without them, and the build's file log (<see cref="BuildLog"/>) for a VSTest run that builds with MSBuild, or empty.</returns>
    private static async Task<(TrxResults? Outcome, ProcessResult? Process, string Log, string ResultsDirectory)> RunDotnetTestAsync(
        RepoRoot root, string workingDirectory, string[] arguments, CancellationToken cancellationToken, bool testingPlatform = false, bool assembly = false, TestRun? run = null)
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
            var (result, log) = builds
                ? await BuildOperation.RunLoggedAsync(command, workingDirectory, results, cancellationToken).ConfigureAwait(false)
                : (await ProcessRunner.RunAsync("dotnet", command, workingDirectory, cancellationToken).ConfigureAwait(false), "");
            var outcome = TrxReader.ReadDirectory(results, root.Path) ?? (testingPlatform ? TestingPlatformOutput.Read(result.Output, root.Path) : null);
            // Counts that say nothing failed from a run that exited with a code other than 0 do not explain its exit code.
            if (testingPlatform && outcome is { Failed: 0 } && result.ExitCode != 0)
                outcome = null;
            if (outcome is null && testingPlatform)
                return (null, result.ExitCode == 0 ? null : result, log, results);
            if (outcome is null)
                return (null, result, log, results);
            return (outcome, null, log, results);
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
    ///     The caller's <c>dotnet test</c> arguments with a positional path spelled the way the runner requires:
    ///     Microsoft.Testing.Platform refuses a directory, project or solution given positionally, so a first argument
    ///     naming one becomes <c>--project</c> or <c>--solution</c> with the path. An assembly stays positional, and
    ///     arguments that already name a project or solution pass unchanged.
    /// </summary>
    internal static string[] NormalizeArguments(string workingDirectory, IReadOnlyList<string> arguments)
    {
        string[] normalized = [.. arguments];
        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] == "--results-directory" && i + 1 < normalized.Length)
                normalized[i + 1] = NormalizeResultsDirectory(workingDirectory, normalized[i + 1]);
            else if (normalized[i].StartsWith("--results-directory=", StringComparison.OrdinalIgnoreCase))
                normalized[i] = "--results-directory=" + NormalizeResultsDirectory(workingDirectory, normalized[i]["--results-directory=".Length..]);
        }

        arguments = normalized;
        if (arguments.Count == 0
            || arguments.Any(a => a.StartsWith("--project", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--solution", StringComparison.OrdinalIgnoreCase)))
            return [.. arguments];
        var first = arguments[0];
        if (first.StartsWith('-')
            || first.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return [.. arguments];
        var option = first.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || first.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? "--solution"
            : first.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
                ? "--project"
                : IsDirectory(workingDirectory, first) ? "--project" : null;
        return option is null ? [.. arguments] : [option, first, .. arguments.Skip(1)];
    }

    /// <summary>
    ///     A caller-named results directory as a full path `dotnet test` can use: relative paths are read from the
    ///     working directory, and on Windows a Git Bash spelling (`/c/Users/...` or `/cygdrive/c/...`) becomes its
    ///     Windows path, which the runner would otherwise write nowhere Fuse reads. A path that cannot be resolved
    ///     keeps its given form.
    /// </summary>
    private static string NormalizeResultsDirectory(string workingDirectory, string path)
    {
        var value = OperatingSystem.IsWindows() && FromGitBashPath(path) is { } windows ? windows : path;

        try
        {
            return Path.GetFullPath(value, workingDirectory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return value;
        }
    }

    /// <summary>
    ///     The Windows path a Git Bash spelling names: <c>/c/Users/...</c> and <c>/cygdrive/c/Users/...</c> both become
    ///     <c>C:\Users\...</c>. Null for any other form.
    /// </summary>
    private static string? FromGitBashPath(string path)
    {
        var rest = path.StartsWith("/cygdrive/", StringComparison.OrdinalIgnoreCase) ? path[10..]
            : path.Length > 2 && path[0] == '/' ? path[1..] : null;
        return rest is { Length: > 2 } && char.IsLetter(rest[0]) && rest[1] == '/'
            ? $"{char.ToUpperInvariant(rest[0])}:\\" + rest[2..].Replace('/', '\\')
            : null;
    }

    /// <summary>Whether <paramref name="path"/>, read from <paramref name="workingDirectory"/>, is a directory; a path that cannot be read as one is not.</summary>
    private static bool IsDirectory(string workingDirectory, string path)
    {
        try
        {
            return Directory.Exists(Path.GetFullPath(path, workingDirectory));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    /// <summary>How <see cref="WithoutResults"/> names a run of the user's own <c>dotnet test</c> arguments.</summary>
    internal const string ArgumentsRun = "dotnet test";

    /// <summary>Microsoft.Testing.Platform's exit code for a run in which no test ran, such as one whose filter matched none.</summary>
    internal const int NoTestRanExitCode = 8;

    /// <summary>
    ///     The <c>--settings</c> argument of a VSTest run: a runsettings file in its results folder that carries its filter,
    ///     so a filter of any length leaves the command line short. A shadow run gets one for the project's runsettings
    ///     too, because <c>dotnet test</c> on an assembly does not read the project; a build with MSBuild without a filter
    ///     reads them itself.
    /// </summary>
    private static string[] Settings(TestRun? run, string results, bool assembly)
    {
        if (run is null || run.UsesTestingPlatform || (run.Filter is null && !(assembly && run.RunSettings is not null)))
            return [];
        return ["--settings", RunSettingsFile.Write(results, run.Filter, run.RunSettings)];
    }

    /// <summary>
    ///     The answer for a run without results. Error lines in its output mean the project did not build, and print as a
    ///     failed test build. A Microsoft.Testing.Platform run that exited with <see cref="NoTestRanExitCode"/> ran no
    ///     test, which the answer says. Any other run that exited with a code other than 0 and printed no error line ended
    ///     without results Fuse can read, which the answer says after the end of its output; a Microsoft.Testing.Platform
    ///     run that crashed before its run summary ends this way.
    /// </summary>
    /// <param name="result">The <c>dotnet test</c> process, or null for a Microsoft.Testing.Platform run that exited with 0.</param>
    /// <param name="root">The repository, which error paths are printed relative to.</param>
    /// <param name="run">What ran, as the answer names it: "the test run of" a project, or <see cref="ArgumentsRun"/> for the user's arguments.</param>
    /// <param name="summary">What ran and why, for a run that passed.</param>
    /// <param name="seconds">How long the operation has taken.</param>
    /// <param name="log">The build's file log, or empty, so a failed test build shows what a failed command printed.</param>
    internal static OperationResult WithoutResults(ProcessResult? result, RepoRoot root, string run, string summary, double seconds, string log = "", string? resultsDirectory = null)
    {
        // A Microsoft.Testing.Platform run that passed without printing a run summary, so there are no counts to show.
        if (result is null)
            return new OperationResult(Outcome.Clean, $"fuse: tests passed in {seconds:0.0} s; {summary}");
        if (result.ExitCode == NoTestRanExitCode && BuildOutputParser.Errors(result.Output, root.Path).Count == 0)
        {
            var what = run == ArgumentsRun ? "no test matched the dotnet test arguments" : $"{run} ran no test";
            return new OperationResult(Outcome.ProblemsFound, $"fuse: {what} (exit code {NoTestRanExitCode})");
        }

        if (result.ExitCode != 0 && BuildOutputParser.Errors(result.Output, root.Path).Count == 0)
        {
            var tail = BuildOperation.Tail(result.Output);
            var where = resultsDirectory is null ? "" : $" (no result file in {resultsDirectory})";
            var line = $"fuse: {run} exited with code {result.ExitCode} and produced no results in {seconds:0.0} s{where}";
            return new OperationResult(Outcome.ProblemsFound, tail.Length == 0 ? line : $"{tail}\n{line}");
        }

        var build = BuildOperation.Render(result, root.Path, seconds, "test build", log);
        return build with { Outcome = result.ExitCode == 0 ? Outcome.Clean : Outcome.ProblemsFound };
    }

    /// <summary>One failure as the answer prints it: its name, its message, and its frames.</summary>
    internal static void AppendFailure(StringBuilder text, TestFailure failure)
    {
        text.Append("FAILED ").Append(failure.Name).Append('\n');
        foreach (var line in failure.Message.Split('\n'))
            text.Append("  ").Append(line).Append('\n');
        foreach (var frame in failure.Frames)
            text.Append("  ").Append(frame).Append('\n');
    }

    /// <summary>The answer for runs that wrote TRX results: the failures, then the counts and <paramref name="summary"/>.</summary>
    private static OperationResult Render(TrxResults outcome, string summary, double seconds, string? resultsPath = null)
    {
        var text = new StringBuilder();
        foreach (var failure in outcome.Failures.Take(MaxFailuresShown))
            AppendFailure(text, failure);

        if (outcome.Failures.Count > MaxFailuresShown)
        {
            // Names only past the first ten, so the agent sees up to 110 failing tests by name.
            var rest = outcome.Failures.Skip(MaxFailuresShown).Select(f => f.Name).Distinct().ToList();
            text.Append($"also failed ({outcome.Failures.Count - MaxFailuresShown}):\n");
            foreach (var name in rest.Take(MaxNamesShown))
                text.Append("  ").Append(name).Append('\n');
            if (rest.Count > MaxNamesShown)
                text.Append($"  ... and {rest.Count - MaxNamesShown} more\n");
        }
        var skipped = outcome.Skipped > 0 ? $", {outcome.Skipped} skipped" : "";
        // The file holds every failure; the answer names it only when it left failures or names out.
        var namesCapped = outcome.Failures.Count > MaxFailuresShown
            && outcome.Failures.Skip(MaxFailuresShown).Select(f => f.Name).Distinct().Count() > MaxNamesShown;
        var file = resultsPath is not null && (outcome.Failed > Math.Min(MaxFailuresShown, outcome.Failures.Count) || namesCapped)
            ? $"; all {outcome.Failed} in {resultsPath}"
            : "";
        text.Append($"fuse: {outcome.Failed} failed, {outcome.Passed} passed{skipped} in {seconds:0.0} s; {summary}{file}");
        return new OperationResult(outcome.Failed > 0 ? Outcome.ProblemsFound : Outcome.Clean, text.ToString());
    }
}
