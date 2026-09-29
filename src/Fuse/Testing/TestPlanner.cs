using System.Diagnostics;
using System.Text;
using Fuse.Graph;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Workspace;

namespace Fuse.Testing;

/// <summary>
///     Turns the working-tree changes into a <see cref="TestPlanResult"/>: which test projects to run, which tests in
///     each (<see cref="TestSelector"/>, <see cref="TestFilter"/>), and whether each can be a shadow run
///     (<see cref="ShadowEmitter"/>). It records the sync, the selection and the shadow preparation as phases.
/// </summary>
internal sealed class TestPlanner
{
    private readonly RepoWorkspace _workspace;
    private readonly TestSelector _selector;
    private readonly TestCounter _counter = new();

    public TestPlanner(RepoWorkspace workspace)
    {
        _workspace = workspace;
        _selector = new TestSelector(workspace, TimeProvider.System);
    }

    /// <summary>Plans a test run.</summary>
    /// <param name="scope">The affected tests, or every test.</param>
    /// <param name="phases">Collects how long each phase of this plan took; <see cref="PhaseTimes.None"/> collects nothing.</param>
    /// <param name="cancellationToken">Cancels the plan.</param>
    public async Task<TestPlanResult> PlanAsync(TestScope scope, PhaseTimes phases, CancellationToken cancellationToken)
    {
        var syncing = phases.Start();
        await _workspace.SyncAsync([], cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        var graph = _workspace.Graph;
        var testProjects = graph.Projects.Where(p => p.IsTest).ToList();
        var total = testProjects.Sum(p => _counter.TestsIn(p).Count);
        if (testProjects.Count == 0)
            return new TestPlanResult([], 0, 0, "no test projects in this repository");

        if (scope is TestScope.All)
        {
            var everything = testProjects.Select(p => new PlannedRun(p.Path, p.Name, new RunMode.Build(), null, p.IsTestingPlatform)).ToArray();
            return new TestPlanResult(everything, total, total, $"ran every test in {testProjects.Count} test project(s)");
        }

        var changed = _workspace.Tracker.Changed.Where(p => graph.OwnersOf(p).Count > 0).ToList();
        if (changed.Count == 0)
            return new TestPlanResult([], 0, total, "no C# changes since HEAD, so no test is affected (fuse test --all runs everything)");

        var timer = Stopwatch.StartNew();
        var selections = await _selector.SelectAsync(changed, cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Selection, timer);
        _workspace.Log($"test plan: selection in {timer.ElapsedMilliseconds} ms: {string.Join("; ", selections.Select(s => $"{Path.GetFileNameWithoutExtension(s.Key.Absolute)} {Describe(s.Value)}"))}");
        var runs = new List<PlannedRun>();
        var selected = 0;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var emitter = new ShadowEmitter(_workspace.Root, graph);
        var mirroring = phases.Start();
        foreach (var (path, selection) in selections.OrderBy(s => s.Key.Absolute, StringComparer.Ordinal))
        {
            var node = graph.Find(path);
            if (node is null)
                continue;
            var tests = _counter.TestsIn(node);
            var count = TestCounter.Count(tests, selection);
            // A static count of zero is not proof nothing matches (inherited test methods run under the derived class's name), so only an empty selection is skipped.
            if (selection is TestSelection.Methods { Patterns.Count: 0 })
                continue;
            selected += count;
            if (selection is TestSelection.Whole whole)
                reasons.Add(whole.Reason);

            var filter = TestFilter.For(selection);
            if (node.IsTestingPlatform)
            {
                // Microsoft.Testing.Platform filters differ per framework; run the project whole.
                runs.Add(new PlannedRun(node.Path, node.Name, new RunMode.Build(), null, true));
                continue;
            }

            runs.AddRange(await RunsOfAsync(node, filter, emitter, timer, cancellationToken).ConfigureAwait(false));
        }

        var summary = new StringBuilder($"ran {selected} test(s) affected by your changes out of {total}");
        if (reasons.Count > 0)
            summary.Append(" (whole projects where ").Append(string.Join("; ", reasons)).Append(')');
        summary.Append("; fuse test --all runs everything");
        // The shadow copy and the in-memory emit happen together inside the emitter, so one phase covers both.
        phases.Add(Phase.Mirror, mirroring);
        if (runs.Count == 0)
            return new TestPlanResult([], 0, total, $"no test is affected by the changes (out of {total}); fuse test --all runs everything");
        return new TestPlanResult([.. runs], selected, total, summary.ToString());
    }

    /// <summary>
    ///     The runs of one VSTest project: a shadow run per target framework when every one of them can have one, and
    ///     otherwise one build with MSBuild for the project, which runs every target framework.
    /// </summary>
    private async Task<IReadOnlyList<PlannedRun>> RunsOfAsync(ProjectNode node, string? filter, ShadowEmitter emitter, Stopwatch timer, CancellationToken cancellationToken)
    {
        var variants = RepoWorkspace.ProjectsFor(_workspace.Current, node).ToList();
        var shadows = new List<RunMode.Shadow>();
        foreach (var variant in variants)
        {
            if (await emitter.PrepareAsync(variant, _workspace.Log, cancellationToken).ConfigureAwait(false) is not RunMode.Shadow shadow)
            {
                shadows.Clear();
                break;
            }

            shadows.Add(shadow);
        }

        _workspace.Log($"test plan: {node.Name} [{string.Join(", ", variants.Select(v => v.Name))}] {(shadows.Count == 0 ? "builds with MSBuild" : $"runs from {string.Join(", ", shadows.Select(s => s.Assembly))}")} after {timer.ElapsedMilliseconds} ms");
        if (shadows.Count == 0)
            return [new PlannedRun(node.Path, node.Name, new RunMode.Build(), filter, false)];
        return [.. shadows.Select(s => new PlannedRun(node.Path, node.Name, s, filter, false))];
    }

    /// <summary>A selection as the engine log gives it: "all", or how many patterns it has.</summary>
    private static string Describe(TestSelection selection) =>
        selection is TestSelection.Methods methods ? methods.Patterns.Count + " pattern(s)" : "all";
}
