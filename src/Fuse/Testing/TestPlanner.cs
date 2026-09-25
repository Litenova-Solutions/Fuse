using System.Text;
using Fuse.Graph;
using Fuse.Protocol;
using Fuse.Repo;
using Fuse.Workspace;

namespace Fuse.Testing;

/// <summary>Turns the working-tree changes into a <see cref="TestPlan"/>: which test projects to run, which tests in each, and whether a shadow build can skip MSBuild.</summary>
internal sealed class TestPlanner
{
    // Windows limits a command line to 32,767 characters; stay well under it with the rest of the arguments.
    private const int MaxFilterLength = 8000;

    private readonly RepoWorkspace _workspace;
    private readonly TestSelector _selector;
    private readonly TestCounter _counter = new();

    public TestPlanner(RepoWorkspace workspace)
    {
        _workspace = workspace;
        _selector = new TestSelector(workspace);
    }

    public async Task<TestPlan> PlanAsync(bool all, CancellationToken cancellationToken)
    {
        await _workspace.SyncAsync([], cancellationToken).ConfigureAwait(false);
        var graph = _workspace.Graph;
        var testProjects = graph.Projects.Where(p => p.IsTest).ToList();
        var total = testProjects.Sum(p => _counter.TestsIn(p).Count);
        if (testProjects.Count == 0)
            return new TestPlan([], 0, 0, "no test projects in this repository");

        if (all)
        {
            var everything = testProjects.Select(p => new TestRun(p.Path, p.Name, null, null, p.IsTestingPlatform)).ToArray();
            return new TestPlan(everything, total, total, $"ran every test in {testProjects.Count} test project(s)");
        }

        var changed = _workspace.Tracker.Changed.Where(p => graph.OwnersOf(p).Count > 0).ToList();
        if (changed.Count == 0)
            return new TestPlan([], 0, total, "no C# changes since HEAD, so no test is affected (fuse test --all runs everything)");

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var selection = await _selector.SelectAsync(changed, cancellationToken).ConfigureAwait(false);
        _workspace.Log($"test plan: selection in {timer.ElapsedMilliseconds} ms: {string.Join("; ", selection.Select(s => $"{Path.GetFileNameWithoutExtension(s.Key)} {(s.Value.All ? "all" : s.Value.Patterns.Count + " pattern(s)")}"))}");
        var runs = new List<TestRun>();
        var selected = 0;
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        var emitter = new ShadowEmitter(_workspace.Root, graph);
        foreach (var (path, projectSelection) in selection.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            var node = graph.Find(path);
            if (node is null)
                continue;
            var tests = _counter.TestsIn(node);
            var count = TestCounter.Count(tests, projectSelection);
            // A static count of zero is not proof nothing matches (inherited test methods run under the derived class's name), so only an empty selection is skipped.
            if (!projectSelection.All && projectSelection.Patterns.Count == 0)
                continue;
            selected += count;
            if (projectSelection.AllReason is not null)
                reasons.Add(projectSelection.AllReason);

            var filter = projectSelection.All ? null : Filter(projectSelection.Patterns);
            if (node.IsTestingPlatform)
            {
                // Microsoft.Testing.Platform filters differ per framework; run the project whole.
                runs.Add(new TestRun(node.Path, node.Name, null, null, true));
                continue;
            }

            var variants = RepoWorkspace.ProjectsFor(_workspace.Current, node).ToList();
            var shadows = new List<string>();
            foreach (var variant in variants)
            {
                var shadow = await emitter.TryPrepareAsync(variant, _workspace.Log, cancellationToken).ConfigureAwait(false);
                if (shadow is null)
                {
                    shadows.Clear();
                    break;
                }

                shadows.Add(shadow);
            }

            _workspace.Log($"test plan: {node.Name} [{string.Join(", ", variants.Select(v => v.Name))}] {(shadows.Count == 0 ? "builds with MSBuild" : $"runs from {string.Join(", ", shadows)}")} after {timer.ElapsedMilliseconds} ms");
            if (shadows.Count == 0)
                runs.Add(new TestRun(node.Path, node.Name, null, filter, false));
            else
                runs.AddRange(shadows.Select(s => new TestRun(node.Path, node.Name, s, filter, false)));
        }

        var scope = new StringBuilder($"ran {selected} test(s) affected by your changes out of {total}");
        if (reasons.Count > 0)
            scope.Append(" (whole projects where ").Append(string.Join("; ", reasons)).Append(')');
        scope.Append("; fuse test --all runs everything");
        if (runs.Count == 0)
            return new TestPlan([], 0, total, $"no test reaches the changed code (out of {total}); fuse test --all runs everything");
        return new TestPlan([.. runs], selected, total, scope.ToString());
    }

    /// <summary>A VSTest filter matching any test whose name contains a pattern. Collapses to class prefixes, then to no filter, when too long.</summary>
    internal static string? Filter(IReadOnlyCollection<string> patterns)
    {
        var filter = Join(patterns);
        if (filter.Length <= MaxFilterLength)
            return filter;
        var classes = patterns.Select(p => p.EndsWith('.') ? p : p[..(p.LastIndexOf('.') + 1)]).Distinct().ToList();
        filter = Join(classes);
        return filter.Length <= MaxFilterLength ? filter : null;
    }

    private static string Join(IEnumerable<string> patterns) =>
        string.Join("|", patterns.OrderBy(p => p, StringComparer.Ordinal).Select(p => "FullyQualifiedName~" + Escape(p)));

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
                builder.Append('\\');
            builder.Append(c);
        }

        return builder.ToString();
    }
}
