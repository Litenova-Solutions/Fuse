using System.Text;
using Fuse.Dotnet;
using Fuse.Engine.Client;
using Fuse.Paths;
using Fuse.Protocol;

namespace Fuse.Operations;

/// <summary>
///     Runs tests and prints failures. With no arguments it runs the tests affected by the working-tree changes
///     (planned by the engine); with arguments it passes them to <c>dotnet test</c> and adds result reporting.
///     With <c>--failed</c> it reruns the tests that failed in the last recorded run.
/// </summary>
internal static class TestOperation
{
    private const int MaxFailuresShown = 10;
    private const int MaxNamesShown = 100;

    /// <param name="session">
    ///     The session the run is for, as its harness identifies it, or null. With one, the affected tests leave out the
    ///     changes only other sessions wrote. The pre-shell hook passes it to <c>fuse test</c>.
    /// </param>
    /// <param name="live">
    ///     What the first stage's failures print to as soon as they are known, or null to print nothing until the answer.
    ///     The command line passes its output; the MCP tool passes null and answers once.
    /// </param>
    /// <param name="failed">True to rerun the tests that failed in the last recorded run instead of planning from the changes.</param>
    public static async Task<OperationResult> RunAsync(RepoRoot root, string workingDirectory, IReadOnlyList<string> arguments, bool all, CancellationToken cancellationToken, string? session = null, TextWriter? live = null, bool failed = false)
    {
        var started = Environment.TickCount64;
        if (failed)
            return await RunFailedAsync(root, live, all, arguments, started, cancellationToken).ConfigureAwait(false);
        // Arguments that only say where the tests are and how to build them plan the affected tests in that scope;
        // any other argument chooses the tests itself and passes to dotnet test as it is.
        var scope = arguments.Count > 0 ? TestScopeArguments.Parse(workingDirectory, arguments) : null;
        if (arguments.Count > 0 && scope is null)
        {
            // dotnet test with the caller's arguments builds with MSBuild, so it holds the lock until it exits.
            using var argumentsLock = BuildLock.AcquireForClient(root);
            // On Microsoft.Testing.Platform the arguments go to the test application, which rejects VSTest's logger options.
            var testingPlatform = GlobalJson.UsesTestingPlatform(workingDirectory, root.Path);
            var (outcome, process, log, resultsDirectory) = await RunDotnetTestAsync(root, workingDirectory, NormalizeArguments(workingDirectory, arguments, testingPlatform), cancellationToken, testingPlatform).ConfigureAwait(false);
            const string summary = "ran the tests your dotnet test arguments name";
            return outcome is null
                ? WithoutResults(process, root, ArgumentsRun, summary, Seconds(started), log, resultsDirectory)
                : Render(outcome, summary, Seconds(started), ResultsFile.WriteTestsWithPaths(root, [("", "", outcome)]));
        }

        // Held from before the plan request: preparing the plan makes the engine mirror and emit into the copies the runs
        // start from, which no other build or test in this repository may do at the same time. RunPlanAsync gives it up
        // once its runs hold their copies.
        var buildLock = BuildLock.AcquireForClient(root);
        try
        {
            EngineRequest request = all ? new EngineRequest.PlanAllTests() : new EngineRequest.PlanAffectedTests { Session = session, Projects = scope?.Paths ?? [], BuildOnly = scope?.BuildOnly ?? false };
            var response = await EngineClient.SendAsync(root, request, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            if (response is not EngineResponse.PlanAnswered answered)
                return OperationResult.Unanswered(response, "test plan");
            var plan = answered.Plan;
            // When a runtime is missing the run is blocked, not clean.
            if (plan.Runs.Length == 0 && plan.ContainerGatedTests > 0)
                return WithoutRuntime(plan);
            if (plan.Runs.Length == 0)
                return new OperationResult(Outcome.Clean, $"fuse: {plan.Summary}");
            var failedFirst = FailedFirstSplit.ForPlan(root, plan);
            var held = buildLock;
            buildLock = null;
            return await RunPlanAsync(root, plan, started, cancellationToken, held, scope, live: live, failedFirstByProject: failedFirst).ConfigureAwait(false);
        }
        finally
        {
            buildLock?.Dispose();
        }
    }

    /// <summary>Reruns the tests that failed in the last recorded run, or answers why there is nothing to rerun.</summary>
    private static async Task<OperationResult> RunFailedAsync(RepoRoot root, TextWriter? live, bool all, IReadOnlyList<string> arguments, long started, CancellationToken cancellationToken)
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
            EngineRequest request = new EngineRequest.PlanFailedTests([.. reruns.Select(r => new EngineRequest.FailedProject(r.Project, r.Failed))]);
            var response = await EngineClient.SendAsync(root, request, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
            if (response is not EngineResponse.PlanAnswered answered)
                return OperationResult.Unanswered(response, "test plan");
            var plan = answered.Plan;
            if (plan.Runs.Length == 0)
                return new OperationResult(Outcome.Unanswered, "fuse: none of the tests that failed last time is in a test project in this repository");
            var held = buildLock;
            buildLock = null;
            return await RunPlanAsync(root, plan, started, cancellationToken, held, live: live, failedRerunCount: reruns.Sum(r => r.Failed.Length)).ConfigureAwait(false);
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
    /// <remarks>
    ///     Shadow runs write no build output: each holds the copy it runs from (<see cref="LocalState.HoldTestCopy"/>),
    ///     and the build lock is released while they run, so another client's build or test plan does not wait for them.
    ///     MSBuild runs share obj and bin folders across projects, so they run one after another, holding the lock.
    /// </remarks>
    internal static async Task<OperationResult> RunPlanAsync(RepoRoot root, TestPlan plan, long started, CancellationToken cancellationToken, BuildLock? held = null, TestScopeArguments? scope = null, TextWriter? live = null, int? failedRerunCount = null, IReadOnlyDictionary<string, string[]>? failedFirstByProject = null)
    {
        var buildLock = held;
        var copies = new List<IDisposable>();
        try
        {
            return await RunPlanHoldingAsync(root, plan, started, copies, () =>
            {
                buildLock?.Dispose();
                buildLock = null;
            }, () => buildLock ??= BuildLock.AcquireForClient(root), scope, cancellationToken, live, failedRerunCount, failedFirstByProject).ConfigureAwait(false);
        }
        finally
        {
            foreach (var copy in copies)
                copy.Dispose();
            buildLock?.Dispose();
        }
    }

    private static async Task<OperationResult> RunPlanHoldingAsync(
        RepoRoot root, TestPlan plan, long started, List<IDisposable> copies, Action releaseLock, Func<BuildLock> takeLock, TestScopeArguments? scope, CancellationToken cancellationToken, TextWriter? live = null, int? failedRerunCount = null, IReadOnlyDictionary<string, string[]>? failedFirstByProject = null)
    {
        if (failedFirstByProject is not null
            && await RunStagedAsync(root, plan, started, copies, releaseLock, takeLock, scope, live, failedFirstByProject, cancellationToken).ConfigureAwait(false) is { } staged)
            return staged;

        var groups = plan.Runs.GroupBy(r => root.PathOf(r.Project)).ToList();
        // Whether dotnet test runs the platform in this repository decides how a platform run is started.
        var runner = GlobalJson.UsesTestingPlatform(root.Path, root.Path);
        string[] buildSwitches = scope?.BuildSwitches ?? [];
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
        var perProject = new List<(string Project, string Name, TrxResults Outcome)>();
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
                perProject.Add((group.First().Project, group.First().Name, shadowOutcome));
                continue;
            }

            // A run with MSBuild writes build output, so it waits for the build lock.
            takeLock();
            var run = group.First();
            var (outcome, process, log, _) = run.UsesTestingPlatform
                ? await RunPlatformAsync(root, run, runner, scope, cancellationToken).ConfigureAwait(false)
                : await RunDotnetTestAsync(root, root.Path, [run.Project, "--no-restore", .. buildSwitches], cancellationToken, run: run).ConfigureAwait(false);
            if (outcome is not null)
            {
                aggregate = aggregate.Add(outcome);
                perProject.Add((run.Project, run.Name, outcome));
                continue;
            }

            var answer = WithoutResults(process, root, $"the test run of {run.Name}", plan.Summary, Seconds(started), log);
            if (answer.Outcome == Outcome.Clean)
                passedWithoutResults ??= answer;
            else
                problems.Add(answer);
        }

        if (aggregate.Total == 0 && problems.Count == 0 && passedWithoutResults is not null)
        {
            // A clean run leaves no stale failures behind: without this a passing platform run would keep the last failures recorded.
            ResultsFile.WriteTestsWithPaths(root, []);
            return passedWithoutResults;
        }
        if (aggregate.Total == 0 && problems.Count > 0)
            return Join(problems);
        if (failedRerunCount is not null && aggregate.Total == 0)
            return new OperationResult(Outcome.ProblemsFound, $"fuse: none of the {failedRerunCount} test(s) that failed last time ran; they may have been renamed or removed");

        var shadowRuns = outcomes.Count;
        var mode = shadowRuns == groups.Count ? "without MSBuild" : shadowRuns > 0 ? $"without MSBuild for {shadowRuns} of {groups.Count} project(s)" : "built with MSBuild";
        var resultsPath = aggregate.Total > 0 ? ResultsFile.WriteTestsWithPaths(root, perProject) : null;
        var tests = Render(aggregate, $"{plan.Summary}; {mode}", Seconds(started), resultsPath);
        // The tests' answer comes first, so the last line is a failed build or run whenever there is one.
        return problems.Count == 0 ? tests : Join([tests, .. problems]);
    }

    /// <summary>What one stage of a split run produced: its combined results, answers, and which projects ran as shadow runs.</summary>
    private sealed record StageOutcome(TrxResults Aggregate, List<(string Project, string Name, TrxResults Outcome)> PerProject, List<OperationResult> Problems, OperationResult? PassedWithoutResults, IReadOnlySet<RepoPath> Shadowed);

    /// <summary>
    ///     Runs a plan whose projects split into a first stage of last run's failures and a second stage of the rest,
    ///     printing each stage's failures to <paramref name="live"/> as soon as its stage ends. Null when no project
    ///     splits, so the caller runs one stage instead. The two stages record once, from their combined results, so the
    ///     summary counts each test once.
    /// </summary>
    private static async Task<OperationResult?> RunStagedAsync(
        RepoRoot root, TestPlan plan, long started, List<IDisposable> copies, Action releaseLock, Func<BuildLock> takeLock,
        TestScopeArguments? scope, TextWriter? live, IReadOnlyDictionary<string, string[]> failedFirstByProject, CancellationToken cancellationToken)
    {
        var groups = plan.Runs.GroupBy(r => root.PathOf(r.Project)).ToList();
        var priority = new List<(RepoPath Key, List<TestRun> Runs)>();
        var rest = new List<(RepoPath Key, List<TestRun> Runs)>();
        var single = new List<IGrouping<RepoPath, TestRun>>();
        foreach (var group in groups)
        {
            var runs = group.ToList();
            if (failedFirstByProject.TryGetValue(runs[0].Project, out var names)
                && runs.All(r => !r.UsesTestingPlatform)
                && runs.All(r => r.Filter == runs[0].Filter)
                && runs[0].Filter is string original
                && FailedFirstSplit.TrySplit(original, names, out var priorityFilter, out var restFilter))
            {
                priority.Add((group.Key, runs.Select(r => r with { Filter = priorityFilter }).ToList()));
                rest.Add((group.Key, runs.Select(r => r with { Filter = restFilter }).ToList()));
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
                copies.Add(LocalState.HoldTestCopy(Path.GetDirectoryName(shadowMode.Assembly)!));
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
        var first = await RunStageAsync(root, priority, runner, buildSwitches, plan.Summary, started, takeLock, scope, cancellationToken).ConfigureAwait(false);
        if (live is not null)
        {
            live.Write(RenderFailures(first.Aggregate));
            live.Flush();
        }

        releaseLock();
        var secondGroups = rest.Concat(single.Select(g => (g.Key, g.ToList()))).ToList();
        var second = await RunStageAsync(root, secondGroups, runner, buildSwitches, plan.Summary, started, takeLock, scope, cancellationToken).ConfigureAwait(false);
        if (live is not null)
        {
            live.Write(RenderFailures(second.Aggregate));
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
            return Join(problems);

        var perProject = new List<(string Project, string Name, TrxResults Outcome)>();
        foreach (var group in groups)
        {
            var outcomes = first.PerProject.Concat(second.PerProject).Where(p => p.Project == group.First().Project).ToList();
            if (outcomes.Count > 0)
                perProject.Add((group.First().Project, group.First().Name, outcomes.Select(o => o.Outcome).Aggregate(TrxResults.Empty, (a, b) => a.Add(b))));
        }

        // A split project ran without MSBuild only when both its stages did.
        var shadowRuns = priority.Count(p => first.Shadowed.Contains(p.Key) && second.Shadowed.Contains(p.Key)) + single.Count(g => second.Shadowed.Contains(g.Key));
        var mode = shadowRuns == groups.Count ? "without MSBuild" : shadowRuns > 0 ? $"without MSBuild for {shadowRuns} of {groups.Count} project(s)" : "built with MSBuild";
        var resultsPath = aggregate.Total > 0 ? ResultsFile.WriteTestsWithPaths(root, perProject) : null;
        if (live is not null)
        {
            var summaryOnly = new OperationResult(
                aggregate.Failed > 0 ? Outcome.ProblemsFound : Outcome.Clean,
                RenderSummary(aggregate, $"{plan.Summary}; {mode}", Seconds(started), resultsPath));
            return problems.Count == 0 ? summaryOnly : Join([summaryOnly, .. problems]);
        }

        var tests = Render(aggregate, $"{plan.Summary}; {mode}", Seconds(started), resultsPath);
        // The tests' answer comes first, so the last line is a failed build or run whenever there is one.
        return problems.Count == 0 ? tests : Join([tests, .. problems]);
    }

    /// <summary>Runs one stage's groups: its shadow runs in parallel without the lock, then the rest one after another with it.</summary>
    private static async Task<StageOutcome> RunStageAsync(
        RepoRoot root, IReadOnlyList<(RepoPath Key, List<TestRun> Runs)> stage, bool runner, string[] buildSwitches, string summary, long started,
        Func<BuildLock> takeLock, TestScopeArguments? scope, CancellationToken cancellationToken)
    {
        var shadowGroups = stage.Where(g => g.Runs.All(r => r.Mode is TestRunMode.Shadow && !r.UsesTestingPlatform)).ToList();
        var outcomes = new System.Collections.Concurrent.ConcurrentDictionary<RepoPath, TrxResults>();
        await Parallel.ForEachAsync(
            shadowGroups,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            async (group, ct) =>
            {
                var results = await Task.WhenAll(group.Runs.Select(run => RunDotnetTestAsync(
                    root, root.Path, [((TestRunMode.Shadow)run.Mode).Assembly], ct, assembly: true, run: run))).ConfigureAwait(false);
                // A shadow run that could not start sends the project back through MSBuild below.
                if (results.Any(r => r.Outcome is null))
                    return;
                outcomes[group.Key] = results.Aggregate(TrxResults.Empty, (sum, r) => sum.Add(r.Outcome!));
            }).ConfigureAwait(false);

        var aggregate = TrxResults.Empty;
        var perProject = new List<(string Project, string Name, TrxResults Outcome)>();
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

            takeLock();
            var run = runs[0];
            var (outcome, process, log, _) = run.UsesTestingPlatform
                ? await RunPlatformAsync(root, run, runner, scope, cancellationToken).ConfigureAwait(false)
                : await RunDotnetTestAsync(root, root.Path, [run.Project, "--no-restore", .. buildSwitches], cancellationToken, run: run).ConfigureAwait(false);
            if (outcome is not null)
            {
                aggregate = aggregate.Add(outcome);
                perProject.Add((run.Project, run.Name, outcome));
                continue;
            }

            var answer = WithoutResults(process, root, $"the test run of {run.Name}", summary, Seconds(started), log);
            if (answer.Outcome == Outcome.Clean)
                passedWithoutResults ??= answer;
            else
                problems.Add(answer);
        }

        return new StageOutcome(aggregate, perProject, problems, passedWithoutResults, outcomes.Keys.ToHashSet());
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
    ///     Runs one project on Microsoft.Testing.Platform, with its framework's filter. With the repository's
    ///     <c>global.json</c> opt-in, <c>dotnet test</c> runs it. Without one, <c>dotnet test</c> cannot reach it: the
    ///     project has no VSTest target to run (it exits 0 having run nothing, or its targets refuse), and
    ///     <c>--project</c> is not a switch of that command. The project then builds and its test application runs
    ///     directly from what the build wrote, once per target framework, with the output folder as its working
    ///     directory, as a test host runs it. A filtered run that ran no test is answered as the platform's own
    ///     no-test run, never as a pass.
    /// </summary>
    private static async Task<(TrxResults? Outcome, ProcessResult? Process, string Log, string ResultsDirectory)> RunPlatformAsync(
        RepoRoot root, TestRun run, bool runner, TestScopeArguments? scope, CancellationToken cancellationToken)
    {
        string[] switches = scope?.BuildSwitches ?? [];
        if (runner || run.TargetPaths.Length == 0)
        {
            var ran = await RunDotnetTestAsync(root, root.Path, ["--project", run.Project, "--no-restore", .. switches, .. run.FilterArguments], cancellationToken, testingPlatform: true, run: run).ConfigureAwait(false);
            return FilteredNoTest(run, ran);
        }

        var results = Path.Combine(root.StateDirectory, "results", Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // --no-build runs what an earlier build wrote, as dotnet test --no-build does.
            var (built, log) = switches.Contains("--no-build")
                ? (new ProcessResult(0, ""), "")
                : await BuildOperation.RunLoggedAsync(["build", run.Project, "--no-restore", "-nologo", "-tl:off", .. switches.Where(s => s != "--no-build")], root.Path, results, cancellationToken).ConfigureAwait(false);
            if (built.ExitCode != 0)
                return (null, built, log, results);
            var targets = run.TargetPaths
                .Select(t => scope?.Configuration is { } configuration ? ForConfiguration(t, configuration) : t)
                .Where(t => scope?.Framework is not { } framework || HasSegment(t, framework))
                .Where(File.Exists)
                .ToList();
            if (targets.Count == 0)
            {
                // The build wrote none of the applications evaluation named; dotnet test says so in its own words.
                var ran = await RunDotnetTestAsync(root, root.Path, ["--project", run.Project, "--no-restore", .. switches, .. run.FilterArguments], cancellationToken, testingPlatform: true, run: run).ConfigureAwait(false);
                return FilteredNoTest(run, ran);
            }

            string[] settings = run.Filter is not null || run.RunSettings is not null
                ? ["--settings", RunSettingsFile.Write(results, run.Filter, run.RunSettings)]
                : [];
            var aggregate = TrxResults.Empty;
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
                var process = await ProcessRunner.RunAsync(file, [.. arguments], Path.GetDirectoryName(target)!, cancellationToken).ConfigureAwait(false);
                var outcome = TestingPlatformOutput.Read(process.Output, root.Path) ?? XUnitOutput.Read(process.Output, root.Path);
                // Counts that say nothing failed from a run that exited with a code other than 0 do not explain its exit code.
                if (outcome is { Failed: 0 } && process.ExitCode != 0)
                    outcome = null;
                if (outcome is null)
                    return (null, process.ExitCode == 0 ? null : process, log, results);
                aggregate = aggregate.Add(outcome);
                last = process;
            }

            if (aggregate.Total == 0 && (run.Filter is not null || run.FilterArguments.Length > 0))
                return (null, new ProcessResult(NoTestRanExitCode, last?.Output ?? ""), log, results);
            return (aggregate, null, log, results);
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
    ///     A built test application's path under <paramref name="configuration"/>: evaluation names the default
    ///     configuration's output, so the segment a build writes the named one to takes its place.
    /// </summary>
    private static string ForConfiguration(string target, string configuration)
    {
        var parts = target.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var bin = Array.FindIndex(parts, p => p.Equals("bin", StringComparison.OrdinalIgnoreCase));
        if (bin < 0 || bin + 1 >= parts.Length)
            return target;
        parts[bin + 1] = configuration;
        return string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>Whether <paramref name="path"/> has a segment that is exactly <paramref name="segment"/>.</summary>
    private static bool HasSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.Equals(segment, StringComparison.OrdinalIgnoreCase));

    /// <summary>A filtered platform run that ran no test, answered as the platform's own no-test run; any other run unchanged.</summary>
    private static (TrxResults? Outcome, ProcessResult? Process, string Log, string ResultsDirectory) FilteredNoTest(
        TestRun run, (TrxResults? Outcome, ProcessResult? Process, string Log, string ResultsDirectory) ran) =>
        ran.Outcome is { Total: 0 } && (run.Filter is not null || run.FilterArguments.Length > 0)
            ? (null, new ProcessResult(NoTestRanExitCode, ""), ran.Log, ran.ResultsDirectory)
            : ran;

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
    ///     Microsoft.Testing.Platform refuses a project, solution or directory given positionally, so on it a first
    ///     argument naming one becomes <c>--project</c> or <c>--solution</c> with the path. VSTest reads a positional
    ///     path and refuses <c>--project</c>, so on VSTest the path stays positional. An assembly stays positional on
    ///     both, and arguments that already name a project or solution pass unchanged.
    /// </summary>
    internal static string[] NormalizeArguments(string workingDirectory, IReadOnlyList<string> arguments, bool testingPlatform = true)
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
        if (!testingPlatform
            || arguments.Count == 0
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

    /// <summary>How <see cref="WithoutResults"/> names a run of the user's own <c>dotnet test</c> arguments.</summary>
    internal const string ArgumentsRun = "dotnet test";

    /// <summary>Microsoft.Testing.Platform's exit code for a run in which no test ran, such as one whose filter matched none.</summary>
    internal const int NoTestRanExitCode = 8;

    /// <summary>
    ///     The <c>--settings</c> argument of a run whose filter is a VSTest expression (a VSTest run, or MSTest and NUnit
    ///     on Microsoft.Testing.Platform): a runsettings file in its results folder that carries its filter,
    ///     so a filter of any length leaves the command line short. A shadow run gets one for the project's runsettings
    ///     too, because <c>dotnet test</c> on an assembly does not read the project; a build with MSBuild without a filter
    ///     reads them itself.
    /// </summary>
    private static string[] Settings(TestRun? run, string results, bool assembly)
    {
        if (run is null || (run.Filter is null && !(assembly && run.RunSettings is not null)))
            return [];
        return ["--settings", RunSettingsFile.Write(results, run.Filter, run.RunSettings)];
    }

    /// <summary>
    ///     The answer when every selected test needs a container runtime that is not running: no process starts, which
    ///     reads as blocked rather than clean, and the answer names the fix.
    /// </summary>
    internal static OperationResult WithoutRuntime(TestPlan plan) =>
        new(Outcome.ProblemsFound, $"fuse: no test ran: {plan.ContainerGatedTests} test(s) selected in {string.Join(", ", plan.ContainerGatedProjects)} need a container runtime, and none is running; start Docker or Podman and run fuse test again");

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

    /// <summary>The answer for runs with results: the failures grouped by their shared first line, then the counts and <paramref name="summary"/>.</summary>
    internal static OperationResult Render(TrxResults outcome, string summary, double seconds, string? resultsPath = null)
    {
        var text = new StringBuilder();
        text.Append(RenderFailures(outcome));
        text.Append(RenderSummary(outcome, summary, seconds, resultsPath));
        return new OperationResult(outcome.Failed > 0 ? Outcome.ProblemsFound : Outcome.Clean, text.ToString());
    }

    /// <summary>The failures of <paramref name="outcome"/> as the answer prints them: each group under its shared line, then the names past the detail budget.</summary>
    internal static string RenderFailures(TrxResults outcome)
    {
        var text = new StringBuilder();
        var (detailed, restNames, restCount) = FailureGroups.Split(outcome.Failures, MaxFailuresShown);
        foreach (var (header, failure) in detailed)
        {
            if (header is not null)
                text.Append(header).Append('\n');
            AppendFailure(text, failure);
        }

        if (restCount > 0)
        {
            // Names only past the detail budget, so the agent sees up to 110 failing tests by name.
            text.Append($"also failed ({restCount}):\n");
            foreach (var name in restNames.Take(MaxNamesShown))
                text.Append("  ").Append(name).Append('\n');
            if (restNames.Count > MaxNamesShown)
                text.Append($"  ... and {restNames.Count - MaxNamesShown} more\n");
        }

        return text.ToString();
    }

    /// <summary>The last line for runs with results: the counts, <paramref name="summary"/>, and the results file when the answer left failures or names out.</summary>
    internal static string RenderSummary(TrxResults outcome, string summary, double seconds, string? resultsPath = null)
    {
        var skipped = outcome.Skipped > 0 ? $", {outcome.Skipped} skipped" : "";
        // The file holds every failure; the answer names it only when it left failures or names out.
        var (_, restNames, _) = FailureGroups.Split(outcome.Failures, MaxFailuresShown);
        var namesCapped = restNames.Count > MaxNamesShown;
        var file = resultsPath is not null && (outcome.Failed > Math.Min(MaxFailuresShown, outcome.Failures.Count) || namesCapped)
            ? $"; all {outcome.Failed} in {resultsPath}"
            : "";
        return $"fuse: {outcome.Failed} failed, {outcome.Passed} passed{skipped} in {seconds:0.0} s; {summary}{file}";
    }
}
