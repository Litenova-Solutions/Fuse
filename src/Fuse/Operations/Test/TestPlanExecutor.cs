using Fuse.Dotnet.Processes;
using Fuse.Dotnet.Test;
using Fuse.Operations.Build;
using Fuse.Paths;
using Fuse.Protocol.Testing;
namespace Fuse.Operations.Test;

/// <summary>
///     Runs a test plan: the reuse check, the parallel shadow runs and the staged failed-first runs, and the
///     lock holding around them. Shadow runs hold their copies and release the build lock while they run;
///     MSBuild runs hold it one after another.
/// </summary>
internal sealed class TestPlanExecutor
{
    private readonly PlanExecutionOptions options;

    private TestPlanExecutor(PlanExecutionOptions options)
    {
        this.options = options;
    }

    /// <summary>
    ///     What one plan run carries: the plan and where it runs, the lock the caller holds, the scope and live
    ///     output, the failed run it reruns or splits, and the knobs every run honors.
    /// </summary>
    internal sealed record PlanExecutionOptions(
        RepoRoot Root,
        TestPlan Plan,
        long Started,
        BuildLock? Held,
        TestScopeArguments? Scope,
        TextWriter? Live,
        int? FailedRerunCount,
        IReadOnlyDictionary<string, string[]>? FailedFirstByProject,
        bool Clean,
        bool NoCache,
        int MaxFailures,
        TimeSpan? InactivityTimeout);

    /// <summary>What one stage of a split run produced: its combined results, answers, and which projects ran as shadow runs.</summary>
    private sealed record StageOutcome(TestRunSummary Aggregate, List<(string Project, string Name, TestRunSummary Outcome)> PerProject, List<OperationResult> Problems, OperationResult? PassedWithoutResults, IReadOnlySet<RepoPath> Shadowed);

    /// <summary>Runs <paramref name="options"/> to one answer for every run of its plan.</summary>
    internal static Task<OperationResult> ExecuteAsync(PlanExecutionOptions options, CancellationToken cancellationToken)
    {
        var executor = new TestPlanExecutor(options);
        return executor.RunAsync(cancellationToken);
    }

    private async Task<OperationResult> RunAsync(CancellationToken cancellationToken)
    {
        var root = this.options.Root;
        var plan = this.options.Plan;
        var started = this.options.Started;
        var stamp = await BuildResultsFile.StampForReuseAsync(root, cancellationToken).ConfigureAwait(false);
        var planKey = stamp is null ? null : LastTestResults.PlanKey(plan, this.options.Scope);
        if (!this.options.Clean && !this.options.NoCache && stamp is not null && planKey is not null && LastTestResults.ReusedTestRun(root, stamp.Value, planKey, this.options.Live) is { } reused)
            return reused;
        (string TreeHash, string FullHash, string AssetsHash, string PlanKey)? key = null;
        if (stamp is not null && planKey is not null)
            key = (stamp.Value.TreeHash, stamp.Value.FullHash, stamp.Value.AssetsHash, planKey);
        var buildLock = this.options.Held;
        var copies = new List<IDisposable>();
        try
        {
            return await RunPlanHoldingAsync(root, plan, started, copies, () =>
            {
                buildLock?.Dispose();
                buildLock = null;
            }, () => buildLock ??= BuildLock.AcquireForClient(root), this.options.Scope, cancellationToken, this.options.Live, this.options.FailedRerunCount, this.options.FailedFirstByProject, key, this.options.MaxFailures, this.options.NoCache, this.options.InactivityTimeout).ConfigureAwait(false);
        }
        finally
        {
            foreach (var copy in copies)
                copy.Dispose();
            buildLock?.Dispose();
        }
    }

    private static async Task<OperationResult> RunPlanHoldingAsync(
        RepoRoot root,
        TestPlan plan,
        long started,
        List<IDisposable> copies,
        Action releaseLock,
        Func<BuildLock> takeLock,
        TestScopeArguments? scope,
        CancellationToken cancellationToken,
        TextWriter? live = null,
        int? failedRerunCount = null,
        IReadOnlyDictionary<string, string[]>? failedFirstByProject = null,
        (string TreeHash, string FullHash, string AssetsHash, string PlanKey)? reuse = null,
        int maxFailures = TestOperation.DefaultMaxFailures,
        bool noCache = false,
        TimeSpan? inactivityTimeout = null)
    {
        if (failedFirstByProject is not null
            && await RunStagedAsync(root, plan, started, copies, releaseLock, takeLock, scope, live, failedFirstByProject, cancellationToken, reuse, maxFailures, noCache, inactivityTimeout).ConfigureAwait(false) is { } staged)
            return staged;

        var groups = plan.Runs.GroupBy(r => root.PathOf(r.Project)).ToList();
        // Whether dotnet test runs the platform in this repository decides how a platform run is started.
        var runner = GlobalJson.UsesTestingPlatform(root.Path, root.Path);
        string[] buildSwitches = scope?.BuildSwitches ?? [];
        var shadowGroups = groups.Where(g => g.All(r => r.Mode is TestRunMode.Shadow)).ToList();
        if (shadowGroups.Count > 0)
        {
            // Every copy is held before the lock goes, so no plan prepared after it writes a copy these runs start from.
            foreach (var run in shadowGroups.SelectMany(g => g))
            {
                var folder = Path.GetDirectoryName(((TestRunMode.Shadow)run.Mode).Assembly);
                ArgumentNullException.ThrowIfNull(folder);
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

        var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<RepoPath, TestRunSummary>();
        var timeouts = new System.Collections.Concurrent.ConcurrentDictionary<RepoPath, (ProcessResult Process, string? Framework, string Name)>();
        await Parallel.ForEachAsync(
            shadowGroups,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (group, ct) =>
            {
                // One process per target framework, in parallel. Every run in a shadow group is a shadow run (VSTest or MTP).
                var results = await Task.WhenAll(group.Select(async run => (Run: run, Ran: await ShadowTestRunner.RunShadowOrCachedAsync(root, run, maxFailures, ct, noCache, inactivityTimeout).ConfigureAwait(false)))).ConfigureAwait(false);
                // A shadow run that hung is reported as timed out: its tests hang, so MSBuild would hang on them too.
                // One that could not start (a test host or adapter problem) sends the whole project through MSBuild below.
                var hung = results.FirstOrDefault(r => r.Ran.Process?.TimedOut is true);
                if (hung.Run is not null)
                {
                    var hungProcess = hung.Ran.Process;
                    ArgumentNullException.ThrowIfNull(hungProcess);
                    timeouts[group.Key] = (hungProcess, hung.Ran.TimedOutFramework ?? scope?.Framework, hung.Run.Name);
                    return;
                }

                if (results.Any(r => r.Ran.Outcome is null))
                    return;
                outcomes[group.Key] = results.Aggregate(TestRunSummary.Empty, (sum, r) => sum.Add(r.Ran.Outcome ?? TestRunSummary.Empty));
            }).ConfigureAwait(false);

        var remainingGroups = groups.Where(g => !outcomes.ContainsKey(g.Key) && !timeouts.ContainsKey(g.Key)).ToList();
        var parallelBuiltSwitches = buildSwitches;
        var runRemainingInParallel = false;
        if (remainingGroups.Count > 0)
        {
            await BuildOperation.YieldMemoryIfTightAsync(root, cancellationToken).ConfigureAwait(false);
            takeLock();
            if (remainingGroups.Count > 1)
            {
                if (buildSwitches.Contains("--no-build"))
                {
                    runRemainingInParallel = true;
                }
                else
                {
                    var traversalProjects = remainingGroups.Select(g => g.First().Project).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var (traversalResult, _, _, _) = await BuildOperation.RunTraversalBuildAsync(
                        root,
                        traversalProjects,
                        ["--no-restore", .. buildSwitches, "-nologo", "-tl:off", "-v:q", "-clp:ErrorsOnly;NoSummary"],
                        root.Path,
                        root.StateDirectory,
                        continueOnError: true,
                        cancellationToken).ConfigureAwait(false);
                    if (traversalResult.ExitCode == 0)
                    {
                        parallelBuiltSwitches = DotnetTestRunner.Built(buildSwitches);
                        runRemainingInParallel = true;
                    }
                }
            }
        }

        var aggregate = TestRunSummary.Empty;
        var perProject = new List<(string Project, string Name, TestRunSummary Outcome)>();
        // A project that did not build, or whose run ended without results, is part of the answer whatever the other
        // projects did. A Microsoft.Testing.Platform run that passed without printing a run summary is kept apart: it has
        // no counts to add.
        var problems = new List<OperationResult>();
        OperationResult? passedWithoutResults = null;

        if (runRemainingInParallel)
        {
            var parallelResults = new System.Collections.Concurrent.ConcurrentDictionary<RepoPath, (TestRunSummary? Outcome, ProcessResult? Process, string Log, TestRun Run, string? TimedOutFramework)>();
            var parallelScope = scope is null
                ? new TestScopeArguments([], parallelBuiltSwitches, false, null, null)
                : scope with { BuildSwitches = parallelBuiltSwitches };
            await Parallel.ForEachAsync(
                remainingGroups,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
                async (group, ct) =>
                {
                    var run = group.First();
                    var res = run.UsesTestingPlatform
                        ? await DotnetTestRunner.RunPlatformAsync(root, run, runner, parallelScope, ct, maxFailures, inactivityTimeout).ConfigureAwait(false)
                        : await DotnetTestRunner.RunDotnetTestAsync(root, root.Path, [run.Project, "--no-restore", .. parallelBuiltSwitches], ct, run: run, maxFailures: maxFailures, inactivityTimeout: inactivityTimeout).ConfigureAwait(false);
                    parallelResults[group.Key] = (res.Outcome, res.Process, res.Log, run, res.TimedOutFramework ?? scope?.Framework);
                }).ConfigureAwait(false);

            foreach (var group in groups)
            {
                if (outcomes.TryGetValue(group.Key, out var shadowOutcome))
                {
                    aggregate = aggregate.Add(shadowOutcome);
                    perProject.Add((group.First().Project, group.First().Name, shadowOutcome));
                    continue;
                }

                if (timeouts.TryGetValue(group.Key, out var hung))
                {
                    problems.Add(TestResultRenderer.WithoutResults(hung.Process, root, $"the test run of {hung.Name}", plan.Summary, TestOperation.Seconds(started), timedOutFramework: hung.Framework, inactivityTimeout: inactivityTimeout));
                    continue;
                }

                var (outcome, process, log, run, timedOutFramework) = parallelResults[group.Key];
                if (outcome is not null)
                {
                    aggregate = aggregate.Add(outcome);
                    perProject.Add((run.Project, run.Name, outcome));
                    continue;
                }

                var answer = TestResultRenderer.WithoutResults(process, root, $"the test run of {run.Name}", plan.Summary, TestOperation.Seconds(started), log, timedOutFramework: timedOutFramework ?? scope?.Framework, inactivityTimeout: inactivityTimeout);
                if (answer.Outcome == Outcome.Clean)
                    passedWithoutResults ??= answer;
                else
                    problems.Add(answer);
            }
        }
        else
        {
            foreach (var group in groups)
            {
                if (outcomes.TryGetValue(group.Key, out var shadowOutcome))
                {
                    aggregate = aggregate.Add(shadowOutcome);
                    perProject.Add((group.First().Project, group.First().Name, shadowOutcome));
                    continue;
                }

                if (timeouts.TryGetValue(group.Key, out var hung))
                {
                    problems.Add(TestResultRenderer.WithoutResults(hung.Process, root, $"the test run of {hung.Name}", plan.Summary, TestOperation.Seconds(started), timedOutFramework: hung.Framework, inactivityTimeout: inactivityTimeout));
                    continue;
                }

                // A run with MSBuild writes build output, so it waits for the build lock.
                takeLock();
                var run = group.First();
                var outcomeResult = run.UsesTestingPlatform
                    ? await DotnetTestRunner.RunPlatformAsync(root, run, runner, scope, cancellationToken, maxFailures, inactivityTimeout).ConfigureAwait(false)
                    : await DotnetTestRunner.RunDotnetTestAsync(root, root.Path, [run.Project, "--no-restore", .. buildSwitches], cancellationToken, run: run, maxFailures: maxFailures, inactivityTimeout: inactivityTimeout).ConfigureAwait(false);
                var (outcome, process, log, _, timedOutFramework) = outcomeResult;
                if (outcome is not null)
                {
                    aggregate = aggregate.Add(outcome);
                    perProject.Add((run.Project, run.Name, outcome));
                    continue;
                }

                var answer = TestResultRenderer.WithoutResults(process, root, $"the test run of {run.Name}", plan.Summary, TestOperation.Seconds(started), log, timedOutFramework: timedOutFramework ?? scope?.Framework, inactivityTimeout: inactivityTimeout);
                if (answer.Outcome == Outcome.Clean)
                    passedWithoutResults ??= answer;
                else
                    problems.Add(answer);
            }
        }

        if (remainingGroups.Count > 0)
            await BuildOperation.ShutdownBuildServerIfMemoryTightAsync(root.Path).ConfigureAwait(false);

        if (aggregate.Total == 0 && problems.Count == 0 && passedWithoutResults is not null)
        {
            // A clean run leaves no stale failures behind: without this a passing platform run would keep the last failures recorded.
            ResultsFile.WriteTestsWithPaths(root, []);
            return passedWithoutResults;
        }

        if (aggregate.Total == 0 && problems.Count > 0)
            return TestResultRenderer.Join(problems);
        if (failedRerunCount is not null && aggregate.Total == 0)
            return new OperationResult(Outcome.ProblemsFound, $"fuse: none of the {failedRerunCount} test(s) that failed last time ran; they may have been renamed or removed");

        var shadowRuns = outcomes.Count;
        var mode = shadowRuns == groups.Count ? "without MSBuild" : shadowRuns > 0 ? $"without MSBuild for {shadowRuns} of {groups.Count} project(s)" : "built with MSBuild";
        var resultsPath = aggregate.Total > 0 ? ResultsFile.WriteTestsWithPaths(root, perProject) : null;
        if (reuse is not null && aggregate.Total > 0)
            LastTestResults.WriteRun(root, reuse.Value.TreeHash, reuse.Value.FullHash, reuse.Value.AssetsHash, reuse.Value.PlanKey, DateTime.UtcNow, TestOperation.Seconds(started), $"{plan.Summary}; {mode}", aggregate, perProject, problems);
        var tests = TestResultRenderer.Render(aggregate, $"{plan.Summary}; {mode}", TestOperation.Seconds(started), resultsPath);
        // The tests' answer comes first, so the last line is a failed build or run whenever there is one.
        return problems.Count == 0 ? tests : TestResultRenderer.Join([tests, .. problems]);
    }

    /// <summary>
    ///     Runs a plan whose projects split into a first stage of last run's failures and a second stage of the rest,
    ///     printing each stage's failures to <paramref name="live"/> as soon as its stage ends. Null when no project
    ///     splits, so the caller runs one stage instead. The two stages record once, from their combined results, so the
    ///     summary counts each test once.
    /// </summary>
    private static async Task<OperationResult?> RunStagedAsync(
        RepoRoot root, TestPlan plan, long started, List<IDisposable> copies, Action releaseLock, Func<BuildLock> takeLock,
        TestScopeArguments? scope, TextWriter? live, IReadOnlyDictionary<string, string[]> failedFirstByProject, CancellationToken cancellationToken, (string TreeHash, string FullHash, string AssetsHash, string PlanKey)? reuse = null, int maxFailures = TestOperation.DefaultMaxFailures, bool noCache = false, TimeSpan? inactivityTimeout = null)
    {
        var groups = plan.Runs.GroupBy(r => root.PathOf(r.Project)).ToList();
        var priority = new List<(RepoPath Key, List<TestRun> Runs)>();
        var rest = new List<(RepoPath Key, List<TestRun> Runs)>();
        var single = new List<IGrouping<RepoPath, TestRun>>();
        foreach (var group in groups)
        {
            var runs = group.ToList();
            // A run served from the slice cache keeps the filter its verdict was keyed by, so it never splits.
            if (failedFirstByProject.TryGetValue(runs[0].Project, out var names)
                && runs.All(r => !r.FromCache)
                && runs.All(r => !r.UsesTestingPlatform)
                && runs.All(r => r.Filter == runs[0].Filter)
                && runs[0].Filter is string original
                && FailedFirstSplit.TrySplit(original, names, out var priorityFilter, out var restFilter))
            {
                // A stage runs under another filter than the plan keyed, so it carries no slice key and stores nothing.
                priority.Add((group.Key, runs.Select(r => r with { Filter = priorityFilter, SliceKey = null }).ToList()));
                rest.Add((group.Key, runs.Select(r => r with { Filter = restFilter, SliceKey = null }).ToList()));
            }
            else
            {
                single.Add(group);
            }
        }

        if (priority.Count == 0)
            return null;

        // Every copy is held before the lock goes, so no plan prepared after it writes a copy these runs start from.
        var staged = priority.Select(p => p.Runs).Concat(rest.Select(r => r.Runs)).SelectMany(r => r).Concat(single.SelectMany(g => g)).ToList();
        var anyShadow = false;
        foreach (var run in staged)
        {
            if (run.Mode is not TestRunMode.Shadow shadowMode)
                continue;
            anyShadow = true;
            try
            {
                var assemblyDirectory = Path.GetDirectoryName(shadowMode.Assembly);
                ArgumentNullException.ThrowIfNull(assemblyDirectory);
                copies.Add(LocalState.HoldTestCopy(assemblyDirectory));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A copy that cannot be held still runs; only a later plan could change it under the run.
            }
        }

        if (anyShadow)
            releaseLock();

        // Whether dotnet test runs the platform in this repository decides how a platform run is started.
        var runner = GlobalJson.UsesTestingPlatform(root.Path, root.Path);
        string[] buildSwitches = scope?.BuildSwitches ?? [];
        var first = await RunStageAsync(root, priority, runner, buildSwitches, plan.Summary, started, takeLock, scope, cancellationToken, maxFailures, noCache, inactivityTimeout).ConfigureAwait(false);
        if (live is not null)
        {
            live.Write(TestResultRenderer.RenderFailures(first.Aggregate));
            live.Flush();
        }

        releaseLock();
        var secondGroups = rest.Concat(single.Select(g => (g.Key, g.ToList()))).ToList();
        var second = await RunStageAsync(root, secondGroups, runner, buildSwitches, plan.Summary, started, takeLock, scope, cancellationToken, maxFailures, noCache, inactivityTimeout).ConfigureAwait(false);
        if (live is not null)
        {
            live.Write(TestResultRenderer.RenderFailures(second.Aggregate));
            live.Flush();
        }

        var aggregate = first.Aggregate.Add(second.Aggregate);
        var problems = first.Problems.Concat(second.Problems).ToList();
        var passedWithoutResults = first.PassedWithoutResults ?? second.PassedWithoutResults;
        if (aggregate.Total == 0 && problems.Count == 0 && passedWithoutResults is not null)
        {
            ResultsFile.WriteTestsWithPaths(root, []);
            return passedWithoutResults;
        }

        if (aggregate.Total == 0 && problems.Count > 0)
            return TestResultRenderer.Join(problems);

        var perProject = new List<(string Project, string Name, TestRunSummary Outcome)>();
        foreach (var group in groups)
        {
            var outcomes = first.PerProject.Concat(second.PerProject).Where(p => p.Project == group.First().Project).ToList();
            if (outcomes.Count > 0)
                perProject.Add((group.First().Project, group.First().Name, outcomes.Select(o => o.Outcome).Aggregate(TestRunSummary.Empty, (a, b) => a.Add(b))));
        }

        // A split project ran without MSBuild only when both its stages did.
        var shadowRuns = priority.Count(p => first.Shadowed.Contains(p.Key) && second.Shadowed.Contains(p.Key)) + single.Count(g => second.Shadowed.Contains(g.Key));
        var mode = shadowRuns == groups.Count ? "without MSBuild" : shadowRuns > 0 ? $"without MSBuild for {shadowRuns} of {groups.Count} project(s)" : "built with MSBuild";
        var resultsPath = aggregate.Total > 0 ? ResultsFile.WriteTestsWithPaths(root, perProject) : null;
        if (reuse is not null && aggregate.Total > 0)
            LastTestResults.WriteRun(root, reuse.Value.TreeHash, reuse.Value.FullHash, reuse.Value.AssetsHash, reuse.Value.PlanKey, DateTime.UtcNow, TestOperation.Seconds(started), $"{plan.Summary}; {mode}", aggregate, perProject, problems);
        if (live is not null)
        {
            var summaryOnly = new OperationResult(
                aggregate.Failed > 0 ? Outcome.ProblemsFound : Outcome.Clean,
                TestResultRenderer.RenderSummary(aggregate, $"{plan.Summary}; {mode}", TestOperation.Seconds(started), resultsPath));
            return problems.Count == 0 ? summaryOnly : TestResultRenderer.Join([summaryOnly, .. problems]);
        }

        var tests = TestResultRenderer.Render(aggregate, $"{plan.Summary}; {mode}", TestOperation.Seconds(started), resultsPath);
        // The tests' answer comes first, so the last line is a failed build or run whenever there is one.
        return problems.Count == 0 ? tests : TestResultRenderer.Join([tests, .. problems]);
    }

    /// <summary>Runs one stage's groups: its shadow runs in parallel without the lock, then the rest one after another with it.</summary>
    private static async Task<StageOutcome> RunStageAsync(
        RepoRoot root, IReadOnlyList<(RepoPath Key, List<TestRun> Runs)> stage, bool runner, string[] buildSwitches, string summary, long started,
        Func<BuildLock> takeLock, TestScopeArguments? scope, CancellationToken cancellationToken, int maxFailures = TestOperation.DefaultMaxFailures, bool noCache = false, TimeSpan? inactivityTimeout = null)
    {
        var shadowGroups = stage.Where(g => g.Runs.All(r => r.Mode is TestRunMode.Shadow)).ToList();
        var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<RepoPath, TestRunSummary>();
        var timeouts = new System.Collections.Concurrent.ConcurrentDictionary<RepoPath, (ProcessResult Process, string? Framework, string Name)>();
        await Parallel.ForEachAsync(
            shadowGroups,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (group, ct) =>
            {
                var results = await Task.WhenAll(group.Runs.Select(async run => (Run: run, Ran: await ShadowTestRunner.RunShadowOrCachedAsync(root, run, maxFailures, ct, noCache, inactivityTimeout).ConfigureAwait(false)))).ConfigureAwait(false);
                // A shadow run that hung is reported as timed out: its tests hang, so MSBuild would hang on them too.
                // One that could not start sends the project back through MSBuild below.
                var hung = results.FirstOrDefault(r => r.Ran.Process?.TimedOut is true);
                if (hung.Run is not null)
                {
                    var hungProcess = hung.Ran.Process;
                    ArgumentNullException.ThrowIfNull(hungProcess);
                    timeouts[group.Key] = (hungProcess, hung.Ran.TimedOutFramework ?? scope?.Framework, hung.Run.Name);
                    return;
                }

                if (results.Any(r => r.Ran.Outcome is null))
                    return;
                outcomes[group.Key] = results.Aggregate(TestRunSummary.Empty, (sum, r) => sum.Add(r.Ran.Outcome ?? TestRunSummary.Empty));
            }).ConfigureAwait(false);

        var aggregate = TestRunSummary.Empty;
        var perProject = new List<(string Project, string Name, TestRunSummary Outcome)>();
        var problems = new List<OperationResult>();
        OperationResult? passedWithoutResults = null;
        foreach (var (key, runs) in stage)
        {
            if (outcomes.TryGetValue(key, out var shadowOutcome))
            {
                aggregate = aggregate.Add(shadowOutcome);
                perProject.Add((runs[0].Project, runs[0].Name, shadowOutcome));
                continue;
            }

            if (timeouts.TryGetValue(key, out var hung))
            {
                problems.Add(TestResultRenderer.WithoutResults(hung.Process, root, $"the test run of {hung.Name}", summary, TestOperation.Seconds(started), timedOutFramework: hung.Framework, inactivityTimeout: inactivityTimeout));
                continue;
            }

            takeLock();
            var run = runs[0];
            var stageResult = run.UsesTestingPlatform
                ? await DotnetTestRunner.RunPlatformAsync(root, run, runner, scope, cancellationToken, maxFailures, inactivityTimeout).ConfigureAwait(false)
                : await DotnetTestRunner.RunDotnetTestAsync(root, root.Path, [run.Project, "--no-restore", .. buildSwitches], cancellationToken, run: run, maxFailures: maxFailures, inactivityTimeout: inactivityTimeout).ConfigureAwait(false);
            var (outcome, process, log, _, timedOutFramework) = stageResult;
            if (outcome is not null)
            {
                aggregate = aggregate.Add(outcome);
                perProject.Add((run.Project, run.Name, outcome));
                continue;
            }

            var answer = TestResultRenderer.WithoutResults(process, root, $"the test run of {run.Name}", summary, TestOperation.Seconds(started), log, timedOutFramework: timedOutFramework ?? scope?.Framework, inactivityTimeout: inactivityTimeout);
            if (answer.Outcome == Outcome.Clean)
                passedWithoutResults ??= answer;
            else
                problems.Add(answer);
        }

        return new StageOutcome(aggregate, perProject, problems, passedWithoutResults, outcomes.Keys.ToHashSet());
    }
}
