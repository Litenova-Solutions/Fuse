using System.Globalization;
using System.Text;
using Fuse.Client;
using Fuse.Dotnet.Processes;
using Fuse.Dotnet.Test;
using Fuse.Operations.Build;
using Fuse.Operations.Test.Rendering;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Testing;
namespace Fuse.Operations.Test;

/// <summary>
///     Runs tests and prints failures. With no arguments it runs the tests affected by the working-tree changes
///     (planned by the engine); with arguments it passes them to <c>dotnet test</c> and adds result reporting.
///     With <c>--failed</c> it reruns the tests that failed in the last recorded run.
/// </summary>
internal static class TestOperation
{
    /// <summary>
    ///     How long a test process may write no output before its tree is killed and its run is reported as timed out.
    ///     Thirty minutes is over twice the slowest healthy whole test run in the evals (822 seconds in
    ///     selection-CommunityToolkit-20261017-1344), and within the scale of dotnet test's own hang timeout, whose
    ///     default is one hour per test. A healthy run streams build output or prints failures as they happen, so one
    ///     silent this long has hung, however long its suite is.
    /// </summary>
    internal static readonly TimeSpan TestInactivityTimeout = TimeSpan.FromMinutes(30);

    /// <summary>The inactivity timeout a run arms: the override its caller passed, or the timeout every run uses.</summary>
    internal static TimeSpan EffectiveTimeout(TimeSpan? inactivityTimeout) => inactivityTimeout ?? TestInactivityTimeout;

    /// <summary>
    ///     The failure cap without <c>--max-failures</c>: 0, no cap, so every selected test runs. A run that stopped at a
    ///     cap would leave failing tests unreported, which selection exists to prevent; a caller asks for the cap.
    /// </summary>
    internal const int DefaultMaxFailures = 0;

    /// <summary>
    ///     The message on every failure of counts made from watched failure lines instead of a result file: the run
    ///     stopped early after reaching max failures, or ended without results. Such counts are partial, so the slice
    ///     cache never stores them.
    /// </summary>
    internal const string StoppedEarlyMessage = "stopped early after reaching max failures";

    /// <param name="invocation">What the test run runs.</param>
    /// <param name="cancellationToken">Kills the running <c>dotnet test</c> processes.</param>
    public static async Task<OperationResult> RunAsync(TestInvocation invocation, CancellationToken cancellationToken)
    {
        var root = invocation.Root;
        var workingDirectory = invocation.WorkingDirectory;
        var arguments = invocation.Arguments;
        var all = invocation.All;
        var session = invocation.Session;
        var live = invocation.Live;
        var failed = invocation.Failed;
        var clean = invocation.Clean;
        var noCache = invocation.NoCache;
        var maxFailures = invocation.MaxFailures.ToCount();
        var started = Environment.TickCount64;
        if (failed)
            return await RunFailedAsync(root, live, all, arguments, started, cancellationToken, clean, noCache, maxFailures).ConfigureAwait(false);
        // Arguments that only say where the tests are and how to build them plan the affected tests in that scope;
        // any other argument chooses the tests itself and passes to dotnet test as it is.
        TestScopeArguments? scope = null;
        if (arguments.Count > 0 && TestScopeArguments.Parse(workingDirectory, arguments) is ParseResult<TestScopeArguments?>.Parsed parsedScope)
            scope = parsedScope.Value;
        if (arguments.Count > 0 && scope is null)
        {
            // dotnet test with the caller's arguments builds with MSBuild, so it holds the lock until it exits.
            using var argumentsLock = BuildLock.AcquireForClient(root);
            await BuildOperation.YieldMemoryIfTightAsync(root, cancellationToken).ConfigureAwait(false);
            // On Microsoft.Testing.Platform the arguments go to the test application, which rejects VSTest's logger options.
            var testingPlatform = GlobalJson.UsesTestingPlatform(workingDirectory, root.Path);
            var normalized = TestScopeArguments.NormalizeArguments(workingDirectory, arguments, testingPlatform);
            if (testingPlatform && maxFailures > 0 && await BuildOperation.IsNet11OrLaterAsync(workingDirectory, cancellationToken).ConfigureAwait(false)
                && !normalized.Any(a => a.StartsWith("--maximum-failed-tests", StringComparison.OrdinalIgnoreCase)))
            {
                normalized = [.. normalized, "--maximum-failed-tests", maxFailures.ToString(CultureInfo.InvariantCulture)];
            }

            var (outcome, process, log, resultsDirectory, timedOutFramework) = await DotnetTestRunner.RunDotnetTestAsync(root, workingDirectory, normalized, cancellationToken, testingPlatform, maxFailures: maxFailures).ConfigureAwait(false);
            await BuildOperation.ShutdownBuildServerIfMemoryTightAsync(workingDirectory).ConfigureAwait(false);
            const string summary = "ran the tests your dotnet test arguments name";
            return outcome is null
                ? TestResultRenderer.WithoutResults(process, root, TestResultRenderer.ArgumentsRun, summary, Seconds(started), log, resultsDirectory, timedOutFramework)
                : TestResultRenderer.Render(outcome, summary, Seconds(started), ResultsFile.WriteTestsWithPaths(root, [("", "", outcome)]));
        }

        // Held from before the plan request: preparing the plan makes the engine mirror and emit into the copies the runs
        // start from, which no other build or test in this repository may do at the same time. RunPlanAsync gives it up
        // once its runs hold their copies.
        var buildLock = BuildLock.AcquireForClient(root);
        try
        {
            // A routed dotnet test that names a configuration other than Debug with --no-build is a release gate:
            // it runs every test in the named scope with the original switches instead of the affected tests.
            var releaseGate = !all && scope is not null && IsReleaseGate(scope);
            EngineRequest request = all || releaseGate
                ? new EngineRequest.PlanAllTests { Projects = scope?.Paths ?? [], BuildOnly = scope?.BuildOnly ?? false, Clean = clean, NoCache = noCache, MaxFailures = maxFailures }
                : new EngineRequest.PlanAffectedTests { Session = session, Projects = scope?.Paths ?? [], BuildOnly = scope?.BuildOnly ?? false, Clean = clean, NoCache = noCache, MaxFailures = maxFailures };
            var response = await EngineClient.SendAsync(root, request, TimeSpan.FromMinutes(10), cancellationToken, onBusy: BusyNotice.For()).ConfigureAwait(false);
            if (response is not EngineResponse.PlanAnswered answered)
                return OperationResult.Unanswered(response, "test plan");
            var plan = answered.Plan;
            // When a runtime is missing the run is blocked, not clean.
            if (plan.Runs.Length == 0 && plan.ContainerGatedTests > 0)
                return TestResultRenderer.WithoutRuntime(plan);
            if (plan.Runs.Length == 0)
            {
                if (!all && !releaseGate && scope?.Paths.Length > 0)
                    return new OperationResult(Outcome.Clean, ScopedEmptyMessage(scope));
                return new OperationResult(Outcome.Clean, $"fuse: {plan.Summary}");
            }
            var failedFirst = FailedFirstSplit.ForPlan(root, plan);
            var held = buildLock;
            buildLock = null;
            return await RunPlanAsync(root, plan, started, cancellationToken, held, scope, live: live, failedFirstByProject: failedFirst, clean: clean, noCache: noCache, maxFailures: maxFailures).ConfigureAwait(false);
        }
        finally
        {
            buildLock?.Dispose();
        }
    }

    /// <summary>
    ///     Whether <paramref name="scope"/> is a release gate: it names a configuration other than Debug with
    ///     <c>--no-build</c> for named test projects, so the run covers every test in that scope with the original
    ///     switches instead of the affected tests.
    /// </summary>
    internal static bool IsReleaseGate(TestScopeArguments scope) =>
        scope is { Configuration: not null, Paths.Length: > 0 }
        && !scope.Configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase)
        && scope.BuildSwitches.Contains("--no-build");

    /// <summary>The answer when no test in the named scope is affected: which scope ran, and the every-test run that covers it.</summary>
    internal static string ScopedEmptyMessage(TestScopeArguments scope)
    {
        var hint = string.Join(" ", scope.Paths.Select(p => $"--project {p}"));
        return $"fuse: 0 tests affected in {string.Join(", ", scope.Paths.Select(DisplayName))}; run 'fuse test --all {hint}' to run every test in it";
    }

    /// <summary>The scope path as the answer names it: a project or solution file without its extension, else the file or directory name.</summary>
    private static string DisplayName(string path)
    {
        var name = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(path)
            : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Length > 0 ? name : path;
    }

    /// <summary>Reruns the tests that failed in the last recorded run, or answers why there is nothing to rerun.</summary>
    private static async Task<OperationResult> RunFailedAsync(RepoRoot root, TextWriter? live, bool all, IReadOnlyList<string> arguments, long started, CancellationToken cancellationToken, bool clean = false, bool noCache = false, int maxFailures = DefaultMaxFailures)
    {
        if (all || arguments.Count > 0)
            return new OperationResult(Outcome.Unanswered, "fuse: --failed reruns the tests that failed last time and cannot be combined with other arguments; pass the arguments without --failed to choose the scope");
        var record = LastTestResults.Read(root);
        if (record is null)
            return new OperationResult(Outcome.Unanswered, "fuse: no test run is recorded yet; run fuse test first");
        var reruns = record
            .Where(p => p.Project.Length > 0)
            .Select(p => (p.Project, Failed: p.Tests.Where(t => t.Verdict == TestVerdict.Failed).Select(t => t.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray()))
            .Where(x => x.Failed.Length > 0)
            .ToList();
        if (reruns.Count == 0)
        {
            if (record.Any(p => p.Project.Length == 0 && p.Tests.Any(t => t.Verdict == TestVerdict.Failed)))
                return new OperationResult(Outcome.Unanswered, "fuse: the last test run was from dotnet test arguments, so its failures cannot be rerun; run fuse test");
            return new OperationResult(Outcome.Clean, "fuse: the last test run had no failures");
        }

        var buildLock = BuildLock.AcquireForClient(root);
        try
        {
            EngineRequest request = new EngineRequest.PlanFailedTests([.. reruns.Select(r => new EngineRequest.FailedProject(r.Project, r.Failed))]) { Clean = clean, NoCache = noCache, MaxFailures = maxFailures };
            var response = await EngineClient.SendAsync(root, request, TimeSpan.FromMinutes(10), cancellationToken, onBusy: BusyNotice.For()).ConfigureAwait(false);
            if (response is not EngineResponse.PlanAnswered answered)
                return OperationResult.Unanswered(response, "test plan");
            var plan = answered.Plan;
            if (plan.Runs.Length == 0)
                return new OperationResult(Outcome.Unanswered, "fuse: none of the tests that failed last time is in a test project in this repository");
            var held = buildLock;
            buildLock = null;
            return await RunPlanAsync(root, plan, started, cancellationToken, held, live: live, failedRerunCount: reruns.Sum(r => r.Failed.Length), clean: clean, noCache: noCache, maxFailures: maxFailures).ConfigureAwait(false);
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
    /// <param name="live">What the first stage's failures print to as soon as they are known, or null for one answer at the end.</param>
    /// <param name="failedRerunCount">How many failures a <c>--failed</c> rerun asked for, so matching none of them is reported as not found; null for any other run.</param>
    /// <param name="failedFirstByProject">Each split project's priority tests by absolute project path, or null to run one stage.</param>
    /// <param name="inactivityTimeout">
    ///     How long a test process may write no output before its tree is killed and its run is reported as timed out, or
    ///     null for the timeout every run uses. Every output line renews it.
    /// </param>
    internal static Task<OperationResult> RunPlanAsync(
        RepoRoot root,
        TestPlan plan,
        long started,
        CancellationToken cancellationToken,
        BuildLock? held = null,
        TestScopeArguments? scope = null,
        TextWriter? live = null,
        int? failedRerunCount = null,
        IReadOnlyDictionary<string, string[]>? failedFirstByProject = null,
        bool clean = false,
        bool noCache = false,
        int maxFailures = DefaultMaxFailures,
        TimeSpan? inactivityTimeout = null) =>
        TestPlanExecutor.ExecuteAsync(
            new TestPlanExecutor.PlanExecutionOptions(root, plan, started, held, scope, live, failedRerunCount, failedFirstByProject, clean, noCache, maxFailures, inactivityTimeout),
            cancellationToken);

    internal static double Seconds(long started) => (Environment.TickCount64 - started) / 1000.0;

    /// <summary>One failure as the answer prints it: its name, its message, and its frames.</summary>
    internal static void AppendFailure(StringBuilder text, TestFailure failure) =>
        TestResultRenderer.AppendFailure(text, failure);

    /// <summary>
    ///     Runs a test process while watching stdout for failure lines, terminating the process tree early once
    ///     <paramref name="maxFailures"/> distinct failures are observed (after a short grace window for details to
    ///     flush). With no cap it runs the process to the end, as <see cref="ProcessRunner"/> does.
    /// </summary>
    /// <param name="inactivityTimeout">
    ///     How long the process may write no output before its tree is killed and the result reads timed out, or null
    ///     to wait without a timeout. Every output line renews it.
    /// </param>
    internal static Task<(ProcessResult Result, List<string> Failures)> RunWithEarlyStopAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int maxFailures,
        CancellationToken cancellationToken,
        TimeSpan? inactivityTimeout = null) =>
        DotnetTestRunner.RunWithEarlyStopAsync(fileName, arguments, workingDirectory, maxFailures, cancellationToken, inactivityTimeout);
}
