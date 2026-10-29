using System.Diagnostics;
using System.Text;
using Fuse.Dotnet;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing;

/// <summary>
///     Turns the working-tree changes into a <see cref="TestPlanResult"/>: which test projects to run, which tests in
///     each (<see cref="TestSelector"/>, <see cref="TestFilter"/>), and whether each can be a shadow run
///     (<see cref="ShadowEmitter"/>). It records the sync, the selection, the mirror and the emit as phases.
/// </summary>
/// <remarks>
///     A plan has two parts. <see cref="SelectAsync"/> syncs the workspace and selects the tests, and runs under the
///     request lock, because it brings the shared solutions up to date. <see cref="PrepareAsync"/> mirrors build output and
///     emits the changed assemblies into the shadow folders, and runs after the lock is released, against the snapshot
///     <see cref="SelectAsync"/> took, so a check from another client is not kept waiting behind the emit.
/// </remarks>
internal sealed class TestPlanner
{
    private readonly RepoWorkspace _workspace;
    private readonly TestSelector _selector;
    private readonly TestCounter _counter = new();

    public TestPlanner(RepoWorkspace workspace)
    {
        _workspace = workspace;
        _selector = new TestSelector(workspace, TimeProvider.System);
        Emitter = new ShadowEmitter(workspace);
    }

    /// <summary>The emitter every plan prepares its shadow runs with, which keeps emitted assemblies between plans.</summary>
    public ShadowEmitter Emitter { get; }

    /// <summary>
    ///     Admits one shadow preparation at a time, so two plans never write one shadow folder at once and the emitter's
    ///     cache serves one plan at a time. <c>fuse test</c> already holds <c>build.lock</c> around its plan; this holds for
    ///     any client. A test holds it to keep a plan in its preparation, after the plan has released the request lock.
    /// </summary>
    public SemaphoreSlim Preparation { get; } = new(1, 1);

    /// <summary>Selects the tests and prepares their shadow runs, as one call; the engine calls the two parts itself.</summary>
    /// <param name="scope">The affected tests, or every test.</param>
    /// <param name="phases">Collects how long each phase of this plan took; <see cref="PhaseTimes.None"/> collects nothing.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    public async Task<TestPlanResult> PlanAsync(TestScope scope, PhaseTimes phases, CancellationToken cancellationToken)
    {
        var pending = await SelectAsync(scope, phases, cancellationToken).ConfigureAwait(false);
        return await PrepareAsync(pending, phases, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Syncs the workspace and selects the tests in each test project, taking the snapshot the plan answers for. The
    ///     caller holds the request lock.
    /// </summary>
    /// <param name="scope">The affected tests, or every test.</param>
    /// <param name="phases">Collects the <see cref="Phase.Sync"/> and <see cref="Phase.Selection"/> phases.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    public async Task<PendingPlan> SelectAsync(TestScope scope, PhaseTimes phases, CancellationToken cancellationToken)
    {
        var syncing = phases.Start();
        await _workspace.SyncAsync([], cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        var graph = _workspace.Graph;
        var testProjects = graph.Projects.Where(p => p.IsTest).ToList();
        // Paths the command named limit the plan to the test projects they name; the summary says which.
        var named = scope is TestScope.Affected { NamedPaths.Length: > 0 } namedScope
            ? NamedTestProjects.Resolve(graph, namedScope.NamedPaths)
            : null;
        if (named is not null)
            testProjects = testProjects.Where(named.Contains).ToList();
        var namedNote = named is null ? "" : $"; in {string.Join(", ", testProjects.Select(p => p.Name))}";
        var total = testProjects.Sum(p => _counter.TestsIn(p).Count);
        if (testProjects.Count == 0)
            return PendingPlan.Of(new TestPlanResult([], 0, 0, "no test projects in this repository"), graph);

        if (scope is TestScope.All)
        {
            var everything = testProjects.Select(p => new PlannedRun(p.Path, p.Name, new RunMode.Build(), null, p.UsesTestingPlatform, RunSettingsOf(p), [], p.UsesTestingPlatform ? [.. p.TargetPaths] : [])).ToArray();
            return PendingPlan.Of(new TestPlanResult(everything, total, total, $"ran every test in {testProjects.Count} test project(s)"), graph);
        }

        // A plan for one session leaves out the files only other sessions wrote: the tests their changes affect are
        // theirs to run.
        var leftOut = scope is TestScope.Affected affected ? affected.LeftOut : new HashSet<RepoPath>();
        var owned = _workspace.Tracker.Changed.Where(p => graph.OwnersOf(p).Count > 0).ToList();
        var changed = owned.Where(p => !leftOut.Contains(p)).ToList();
        var others = owned.Count - changed.Count;
        var othersNote = others > 0 ? $"; left out {others} changed file(s) only other sessions wrote" : "";
        if (changed.Count == 0 && others > 0)
            return PendingPlan.Of(new TestPlanResult([], 0, total, $"no C# changes by this session since HEAD, so no test is affected{othersNote}{namedNote} (fuse test --all runs everything)"), graph);
        if (changed.Count == 0)
            return PendingPlan.Of(new TestPlanResult([], 0, total, $"no C# changes since HEAD, so no test is affected{namedNote} (fuse test --all runs everything)"), graph);

        var timer = Stopwatch.StartNew();
        var outcome = await _selector.SelectAsync(changed, cancellationToken).ConfigureAwait(false);
        var selections = outcome.Selections;
        phases.Add(Phase.Selection, timer);
        _workspace.Log($"test plan: selection in {timer.ElapsedMilliseconds} ms: {string.Join("; ", selections.Select(s => $"{Path.GetFileNameWithoutExtension(s.Key.Absolute)} {Describe(s.Value)}"))}");
        var projects = new List<(ProjectNode Node, string? Filter, string[] FilterArguments, IReadOnlyList<Project> Variants)>();
        // How dotnet test reaches a platform project decides how xUnit v3 spells its filter options.
        var runner = GlobalJson.UsesTestingPlatform(_workspace.Root.Path, _workspace.Root.Path);
        var selected = 0;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var collapsed = new List<string>();
        var platformCollapsed = new List<string>();
        var snapshot = _workspace.Current;
        foreach (var (path, selection) in selections.OrderBy(s => s.Key.Absolute, StringComparer.Ordinal))
        {
            var node = graph.Find(path);
            if (node is null || (named is not null && !named.Contains(node)))
                continue;
            var tests = _counter.TestsIn(node);
            // A platform project whose framework Fuse cannot filter runs whole (below), so every test in it counts as run.
            var count = node.UsesTestingPlatform && node.Framework == TestFramework.Unknown && selection is TestSelection.Methods
                ? tests.Count
                : TestCounter.Count(tests, selection);
            // A static count of zero is not proof nothing matches (inherited test methods run under the derived class's name), so only an empty selection is skipped.
            if (selection is TestSelection.Methods { Patterns.Count: 0 })
                continue;
            selected += count;
            if (selection is TestSelection.Whole whole)
                reasons.Add(whole.Reason);

            if (node.UsesTestingPlatform)
            {
                // A platform project has no shadow run; its filter is its framework's, from PlatformRun below.
                var (platformFilter, arguments) = PlatformRun(node, selection, runner, collapsed, platformCollapsed, reasons);
                projects.Add((node, platformFilter, arguments, []));
                continue;
            }

            var filter = TestFilter.For(selection);
            if (filter.Collapse != FilterCollapse.None)
            {
                var patterns = ((TestSelection.Methods)selection).Patterns.Count;
                var form = filter.Collapse == FilterCollapse.ToClasses ? $"{filter.Patterns} class prefix(es)" : "no filter, so the project runs whole";
                _workspace.Log($"test plan: {node.Name} filter of {patterns} pattern(s) is above {TestFilter.MaxPatterns}, collapsed to {form}");
                NoteCollapse(node, filter.Collapse, collapsed);
            }

            projects.Add((node, filter.Expression, [], RepoWorkspace.ProjectsFor(snapshot, node).ToList()));
        }

        if (selections.Count == 0 && outcome.AllSameCode)
            return PendingPlan.Of(new TestPlanResult([], 0, total, $"no test is affected by the changes (out of {total}); the changes compile to the same code as HEAD; fuse test --all runs everything"), graph);

        var summary = new StringBuilder($"ran {selected} test(s) affected by your changes out of {total}");
        if (reasons.Count > 0)
            summary.Append(" (whole projects where ").Append(string.Join("; ", reasons)).Append(')');
        if (collapsed.Count > 0)
            summary.Append("; ran ").Append(string.Join(", ", collapsed)).Append(", because a test filter holds at most ").Append(TestFilter.MaxPatterns).Append(" names");
        if (platformCollapsed.Count > 0)
            summary.Append("; ran ").Append(string.Join(", ", platformCollapsed)).Append(", because a platform filter holds at most ").Append(PlatformFilter.MaxNames).Append(" names");
        summary.Append(othersNote);
        summary.Append(namedNote);
        summary.Append("; fuse test --all runs everything");
        return PendingPlan.ToPrepare(graph, projects, selected, total, summary.ToString(), scope is TestScope.Affected { BuildOnly: true });
    }

    /// <summary>
    ///     Prepares the shadow runs of a plan <see cref="SelectAsync"/> made, from the snapshot it took, and answers the
    ///     plan. It reads no workspace state, so the caller runs it after the request lock is released.
    /// </summary>
    /// <param name="pending">The plan, with its snapshot.</param>
    /// <param name="phases">Collects the <see cref="Phase.Mirror"/> and <see cref="Phase.Emit"/> phases.</param>
    /// <param name="cancellationToken">Cancels emitting.</param>
    public async Task<TestPlanResult> PrepareAsync(PendingPlan pending, PhaseTimes phases, CancellationToken cancellationToken)
    {
        if (pending.Answered is not null)
            return pending.Answered;

        await Preparation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var preparing = Stopwatch.StartNew();
            var emitting = new Stopwatch();
            var runs = new List<PlannedRun>();
            foreach (var (node, filter, filterArguments, variants) in pending.Projects)
            {
                if (node.UsesTestingPlatform)
                    runs.Add(new PlannedRun(node.Path, node.Name, new RunMode.Build(), filter, true, RunSettingsOf(node), filterArguments, [.. node.TargetPaths]));
                else
                    runs.AddRange(await RunsOfAsync(node, filter, variants, pending.Graph, emitting, preparing, pending.BuildOnly, cancellationToken).ConfigureAwait(false));
            }

            // Everything in the preparation but the emit is the mirror: judging which assemblies are stale, and the copy.
            phases.Add(Phase.Mirror, preparing.Elapsed.TotalMilliseconds - emitting.Elapsed.TotalMilliseconds);
            phases.Add(Phase.Emit, emitting.Elapsed.TotalMilliseconds);
            if (runs.Count == 0)
                return new TestPlanResult([], 0, pending.Total, $"no test is affected by the changes (out of {pending.Total}); fuse test --all runs everything");
            return new TestPlanResult([.. runs], pending.Selected, pending.Total, pending.Summary);
        }
        finally
        {
            Preparation.Release();
        }
    }

    /// <summary>
    ///     The runs of one VSTest project: a shadow run per target framework when every one of them can have one, and
    ///     otherwise one build with MSBuild for the project, which runs every target framework.
    /// </summary>
    private async Task<IReadOnlyList<PlannedRun>> RunsOfAsync(ProjectNode node, string? filter, IReadOnlyList<Project> variants, RepoGraph graph, Stopwatch emitting, Stopwatch timer, bool buildOnly, CancellationToken cancellationToken)
    {
        // A configuration or framework was named: only a build with MSBuild honors it, so no shadow run is prepared.
        if (buildOnly)
            return [new PlannedRun(node.Path, node.Name, new RunMode.Build(), filter, false, node.RunSettings, [], [])];

        var shadows = new List<RunMode.Shadow>();
        foreach (var variant in variants)
        {
            if (await Emitter.PrepareAsync(variant, graph, _workspace.Log, emitting, cancellationToken).ConfigureAwait(false) is not RunMode.Shadow shadow)
            {
                shadows.Clear();
                break;
            }

            shadows.Add(shadow);
        }

        _workspace.Log($"test plan: {node.Name} [{string.Join(", ", variants.Select(v => v.Name))}] {(shadows.Count == 0 ? "builds with MSBuild" : $"runs from {string.Join(", ", shadows.Select(s => s.Assembly))}")} after {timer.ElapsedMilliseconds} ms");
        if (shadows.Count == 0)
            return [new PlannedRun(node.Path, node.Name, new RunMode.Build(), filter, false, node.RunSettings, [], [])];
        return [.. shadows.Select(s => new PlannedRun(node.Path, node.Name, s, filter, false, node.RunSettings, [], []))];
    }

    /// <summary>
    ///     The filter of one project on Microsoft.Testing.Platform: the VSTest expression for MSTest and NUnit, which
    ///     accept it there, the framework's own arguments for xUnit v3 and TUnit, and none for a framework Fuse does
    ///     not know, whose project then runs whole with its reason in <paramref name="reasons"/>. A filter that
    ///     collapsed to fit its bound is logged and noted in the list its bound belongs to.
    /// </summary>
    private (string? Filter, string[] Arguments) PlatformRun(ProjectNode node, TestSelection selection, bool runner, List<string> collapsed, List<string> platformCollapsed, HashSet<string> reasons)
    {
        switch (node.Framework)
        {
            case TestFramework.MSTest or TestFramework.NUnit:
                {
                    var filter = TestFilter.For(selection);
                    if (filter.Collapse != FilterCollapse.None)
                    {
                        var form = filter.Collapse == FilterCollapse.ToClasses ? $"{filter.Patterns} class prefix(es)" : "no filter, so the project runs whole";
                        _workspace.Log($"test plan: {node.Name} filter of {((TestSelection.Methods)selection).Patterns.Count} pattern(s) is above {TestFilter.MaxPatterns}, collapsed to {form}");
                        NoteCollapse(node, filter.Collapse, collapsed);
                    }

                    return (filter.Expression, []);
                }
            case TestFramework.XUnit or TestFramework.TUnit:
                {
                    var (arguments, collapse) = PlatformFilter.For(node.Framework, selection, runner);
                    if (collapse != FilterCollapse.None)
                    {
                        var form = collapse == FilterCollapse.ToClasses ? "whole test classes" : "no filter, so the project runs whole";
                        _workspace.Log($"test plan: {node.Name} filter of {((TestSelection.Methods)selection).Patterns.Count} pattern(s) does not fit {PlatformFilter.MaxNames} names, collapsed to {form}");
                        NoteCollapse(node, collapse, platformCollapsed);
                    }

                    return (null, arguments);
                }
            default:
                if (selection is TestSelection.Methods)
                {
                    _workspace.Log($"test plan: {node.Name} runs whole: its test framework is not one Fuse filters");
                    reasons.Add($"{node.Name} runs on a test framework Fuse cannot filter");
                }

                return (null, []);
        }
    }

    /// <summary>Notes what a collapsed filter ran instead, for the summary, in the list its bound belongs to.</summary>
    private static void NoteCollapse(ProjectNode node, FilterCollapse collapse, List<string> collapsed)
    {
        if (collapse == FilterCollapse.ToClasses)
            collapsed.Add($"whole test classes in {node.Name}");
        else if (collapse == FilterCollapse.ToWholeProject)
            collapsed.Add($"all of {node.Name}");
    }

    /// <summary>
    ///     The runsettings file a run of <paramref name="node"/> carries: its own, except on Microsoft.Testing.Platform
    ///     under a framework that does not read one, which is told none.
    /// </summary>
    private static string? RunSettingsOf(ProjectNode node) =>
        node.UsesTestingPlatform && node.Framework is not (TestFramework.MSTest or TestFramework.NUnit) ? null : node.RunSettings;

    /// <summary>A selection as the engine log gives it: "all", or how many patterns it has.</summary>
    private static string Describe(TestSelection selection) =>
        selection is TestSelection.Methods methods ? methods.Patterns.Count + " pattern(s)" : "all";

    /// <summary>
    /// A recorded test name as a filter matches it: a theory row without its argument values.
    /// </summary>
    private static string StripTheoryArgs(string name)
    {
        var args = name.IndexOf('(');
        return args < 0 ? name : name[..args].Trim();
    }

    /// <summary>
    ///     Selects the recorded failures <paramref name="byProject"/> names for a rerun, in the test projects they are
    ///     still in. Projects that are gone or hold no tests are left out and named in the summary, as are the paths
    ///     <paramref name="unknown"/> holds that name no file. The caller holds the request lock.
    /// </summary>
    /// <param name="byProject">Recorded failure names by test project file.</param>
    /// <param name="unknown">Recorded project paths that name no file.</param>
    /// <param name="phases">Collects the <see cref="Phase.Sync"/> and <see cref="Phase.Selection"/> phases.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    public async Task<PendingPlan> SelectFailedAsync(IReadOnlyDictionary<RepoPath, string[]> byProject, IReadOnlyList<string> unknown, PhaseTimes phases, CancellationToken cancellationToken)
    {
        var syncing = phases.Start();
        await _workspace.SyncAsync([], cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        var graph = _workspace.Graph;
        var testProjects = graph.Projects.Where(p => p.IsTest).ToList();
        var total = testProjects.Sum(p => _counter.TestsIn(p).Count);
        if (testProjects.Count == 0)
            return PendingPlan.Of(new TestPlanResult([], 0, 0, "no test projects in this repository"), graph);

        var timer = Stopwatch.StartNew();
        var leftOut = unknown.Where(p => p.Length > 0).Select(p => Path.GetFileNameWithoutExtension(p) ?? "").Where(n => n.Length > 0).ToList();
        var reruns = new List<(ProjectNode Node, string[] Names)>();
        foreach (var (path, tests) in byProject.OrderBy(p => p.Key.Absolute, StringComparer.Ordinal))
        {
            var node = graph.Find(path);
            if (node is null || !node.IsTest)
            {
                leftOut.Add(node?.Name ?? Path.GetFileNameWithoutExtension(path.Absolute));
                continue;
            }

            var names = tests.Select(StripTheoryArgs).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            if (names.Length > 0)
                reruns.Add((node, names));
        }

        var runner = GlobalJson.UsesTestingPlatform(_workspace.Root.Path, _workspace.Root.Path);
        var projects = new List<(ProjectNode Node, string? Filter, string[] FilterArguments, IReadOnlyList<Project> Variants)>();
        var collapsed = new List<string>();
        var platformCollapsed = new List<string>();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var selected = 0;
        var snapshot = _workspace.Current;
        foreach (var (node, names) in reruns)
        {
            TestSelection selection = new TestSelection.Methods([.. names]);
            var tests = _counter.TestsIn(node);
            // A platform project whose framework Fuse cannot filter runs whole, so every test in it counts as run.
            selected += node.UsesTestingPlatform && node.Framework == TestFramework.Unknown
                ? tests.Count
                : TestCounter.Count(tests, selection);
            if (node.UsesTestingPlatform)
            {
                var (platformFilter, arguments) = PlatformRun(node, selection, runner, collapsed, platformCollapsed, reasons);
                projects.Add((node, platformFilter, arguments, []));
                continue;
            }

            var filter = TestFilter.For(selection);
            if (filter.Collapse != FilterCollapse.None)
            {
                var form = filter.Collapse == FilterCollapse.ToClasses ? $"{filter.Patterns} class prefix(es)" : "no filter, so the project runs whole";
                _workspace.Log($"test plan: {node.Name} filter of {names.Length} pattern(s) is above {TestFilter.MaxPatterns}, collapsed to {form}");
                NoteCollapse(node, filter.Collapse, collapsed);
            }

            projects.Add((node, filter.Expression, [], RepoWorkspace.ProjectsFor(snapshot, node).ToList()));
        }

        phases.Add(Phase.Selection, timer);
        var summary = new StringBuilder($"reran {reruns.Sum(r => r.Names.Length)} test(s) that failed last time in {reruns.Count} test project(s)");
        if (reasons.Count > 0)
            summary.Append(" (whole projects where ").Append(string.Join("; ", reasons)).Append(')');
        if (collapsed.Count > 0)
            summary.Append("; ran ").Append(string.Join(", ", collapsed)).Append(", because a test filter holds at most ").Append(TestFilter.MaxPatterns).Append(" names");
        if (platformCollapsed.Count > 0)
            summary.Append("; ran ").Append(string.Join(", ", platformCollapsed)).Append(", because a platform filter holds at most ").Append(PlatformFilter.MaxNames).Append(" names");
        foreach (var name in leftOut.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal))
            summary.Append("; left out ").Append(name).Append(", which is not a test project in this repository");
        if (reruns.Count == 0)
            return PendingPlan.Of(new TestPlanResult([], 0, total, summary.ToString()), graph);
        return PendingPlan.ToPrepare(graph, projects, selected, total, summary.ToString());
    }
}
