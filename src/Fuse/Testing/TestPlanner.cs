using System.Diagnostics;
using System.Text;
using Fuse.Graph;
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
        Emitter = new ShadowEmitter(workspace.Root);
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
        var total = testProjects.Sum(p => _counter.TestsIn(p).Count);
        if (testProjects.Count == 0)
            return PendingPlan.Of(new TestPlanResult([], 0, 0, "no test projects in this repository"), graph);

        if (scope is TestScope.All)
        {
            var everything = testProjects.Select(p => new PlannedRun(p.Path, p.Name, new RunMode.Build(), null, p.UsesTestingPlatform, p.UsesTestingPlatform ? null : p.RunSettings)).ToArray();
            return PendingPlan.Of(new TestPlanResult(everything, total, total, $"ran every test in {testProjects.Count} test project(s)"), graph);
        }

        var changed = _workspace.Tracker.Changed.Where(p => graph.OwnersOf(p).Count > 0).ToList();
        if (changed.Count == 0)
            return PendingPlan.Of(new TestPlanResult([], 0, total, "no C# changes since HEAD, so no test is affected (fuse test --all runs everything)"), graph);

        var timer = Stopwatch.StartNew();
        var selections = await _selector.SelectAsync(changed, cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Selection, timer);
        _workspace.Log($"test plan: selection in {timer.ElapsedMilliseconds} ms: {string.Join("; ", selections.Select(s => $"{Path.GetFileNameWithoutExtension(s.Key.Absolute)} {Describe(s.Value)}"))}");
        var projects = new List<(ProjectNode Node, string? Filter, IReadOnlyList<Project> Variants)>();
        var selected = 0;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var collapsed = new List<string>();
        var snapshot = _workspace.Current;
        foreach (var (path, selection) in selections.OrderBy(s => s.Key.Absolute, StringComparer.Ordinal))
        {
            var node = graph.Find(path);
            if (node is null)
                continue;
            var tests = _counter.TestsIn(node);
            // A Microsoft.Testing.Platform project runs whole (below), so every test in it counts as run.
            var count = node.UsesTestingPlatform ? tests.Count : TestCounter.Count(tests, selection);
            // A static count of zero is not proof nothing matches (inherited test methods run under the derived class's name), so only an empty selection is skipped.
            if (selection is TestSelection.Methods { Patterns.Count: 0 })
                continue;
            selected += count;
            if (selection is TestSelection.Whole whole)
                reasons.Add(whole.Reason);

            if (node.UsesTestingPlatform)
            {
                // Microsoft.Testing.Platform filters differ per framework, so such a project runs whole and has no shadow run.
                projects.Add((node, null, []));
                continue;
            }

            var filter = TestFilter.For(selection);
            if (filter.Collapse != FilterCollapse.None)
            {
                var patterns = ((TestSelection.Methods)selection).Patterns.Count;
                var form = filter.Collapse == FilterCollapse.ToClasses ? $"{filter.Patterns} class prefix(es)" : "no filter, so the project runs whole";
                _workspace.Log($"test plan: {node.Name} filter of {patterns} pattern(s) is above {TestFilter.MaxPatterns}, collapsed to {form}");
                collapsed.Add(filter.Collapse == FilterCollapse.ToClasses ? $"whole test classes in {node.Name}" : $"all of {node.Name}");
            }

            projects.Add((node, filter.Expression, RepoWorkspace.ProjectsFor(snapshot, node).ToList()));
        }

        var summary = new StringBuilder($"ran {selected} test(s) affected by your changes out of {total}");
        if (reasons.Count > 0)
            summary.Append(" (whole projects where ").Append(string.Join("; ", reasons)).Append(')');
        if (collapsed.Count > 0)
            summary.Append("; ran ").Append(string.Join(", ", collapsed)).Append(", because a test filter holds at most ").Append(TestFilter.MaxPatterns).Append(" names");
        summary.Append("; fuse test --all runs everything");
        return PendingPlan.ToPrepare(graph, projects, selected, total, summary.ToString());
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
            foreach (var (node, filter, variants) in pending.Projects)
            {
                if (node.UsesTestingPlatform)
                    runs.Add(new PlannedRun(node.Path, node.Name, new RunMode.Build(), null, true, null));
                else
                    runs.AddRange(await RunsOfAsync(node, filter, variants, pending.Graph, emitting, preparing, cancellationToken).ConfigureAwait(false));
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
    private async Task<IReadOnlyList<PlannedRun>> RunsOfAsync(ProjectNode node, string? filter, IReadOnlyList<Project> variants, RepoGraph graph, Stopwatch emitting, Stopwatch timer, CancellationToken cancellationToken)
    {
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
            return [new PlannedRun(node.Path, node.Name, new RunMode.Build(), filter, false, node.RunSettings)];
        return [.. shadows.Select(s => new PlannedRun(node.Path, node.Name, s, filter, false, node.RunSettings))];
    }

    /// <summary>A selection as the engine log gives it: "all", or how many patterns it has.</summary>
    private static string Describe(TestSelection selection) =>
        selection is TestSelection.Methods methods ? methods.Patterns.Count + " pattern(s)" : "all";
}
