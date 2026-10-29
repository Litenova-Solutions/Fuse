using Fuse.Paths;
using Fuse.Testing.Model;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing;

/// <summary>
///     Selects the tests that can observe the working-tree changes. It takes the <see cref="TypeWalk"/>'s class-level
///     answer, and refines it to test methods with the <see cref="MemberWalk"/> when few classes are reachable and the
///     walk finishes within its budget.
/// </summary>
/// <remarks>
///     A missed failing test is the one outcome selection must not produce; running extra tests only costs time. So when
///     the refinement would cost more than it saves, or does not finish, the class-level answer stands.
/// </remarks>
internal sealed class TestSelector
{
    // Above this many reachable test classes the refinement rarely finishes within the walk's own 300-symbol and
    // 8-second budget, and only a walk that finishes has visited every caller, which is what makes its answer safe.
    private const int RefineThreshold = 100;

    private readonly RepoWorkspace _workspace;
    private readonly TimeProvider _time;
    private readonly TypeWalk _types;

    /// <param name="workspace">The engine's workspace, which the selector loads the changed projects and their dependents into.</param>
    /// <param name="time">Measures the member walk against its budget.</param>
    public TestSelector(RepoWorkspace workspace, TimeProvider time)
    {
        _workspace = workspace;
        _time = time;
        _types = new TypeWalk(workspace);
    }

    /// <summary>Selects tests for <paramref name="changedFiles"/>; the result holds each test project file's selection.</summary>
    /// <param name="changedFiles">Changed files that an evaluated project owns.</param>
    /// <param name="cancellationToken">Cancels loading and both walks.</param>
    public async Task<SelectionResult> SelectAsync(IReadOnlyList<RepoPath> changedFiles, CancellationToken cancellationToken)
    {
        var graph = _workspace.Graph;
        var changedProjects = changedFiles.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();
        var reachedProjects = changedProjects.Concat(changedProjects.SelectMany(graph.DependentsOf)).DistinctBy(p => p.Path).ToList();
        if (!reachedProjects.Any(p => p.IsTest))
            return new SelectionResult(new Dictionary<RepoPath, TestSelection>(), 0, false);
        await _workspace.EnsureLoadedAsync(reachedProjects, cancellationToken).ConfigureAwait(false);

        var solution = _workspace.Current;
        var reachedRoslynProjects = reachedProjects.SelectMany(n => RepoWorkspace.ProjectsFor(solution, n)).ToList();
        var walk = new MemberWalk(_workspace, solution, reachedRoslynProjects, _time);
        var seeds = new List<(Document Document, SyntaxNode Node)>();
        foreach (var path in changedFiles)
        {
            if (!path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                // A Razor file (.razor, .cshtml) has no declaration either walk can start from, so every test project
                // that depends on its project runs whole.
                foreach (var owner in graph.OwnersOf(path))
                    walk.SelectDependentsWhole(owner, $"{path.FileName} changed");
                continue;
            }

            seeds.AddRange(await walk.SeedAsync(path, cancellationToken).ConfigureAwait(false));
        }

        if (walk.DroppedSameCode > 0)
            _workspace.Log($"test selection: {walk.DroppedSameCode} of {walk.ChangedDeclarations} changed declaration(s) compile to the same code as HEAD; selecting no tests for them");

        // The projects the seeds selected whole start the class-level answer, and a whole selection from the type walk
        // replaces theirs. The type walk's methods add nothing to a whole project.
        var seededWhole = walk.Selections.Where(s => s.Value is TestSelection.Whole).ToList();
        var classLevel = new Dictionary<RepoPath, TestSelection>(seededWhole);
        foreach (var (path, selection) in await _types.SelectAsync(reachedRoslynProjects, seeds, cancellationToken).ConfigureAwait(false))
        {
            if (selection is TestSelection.Whole || !classLevel.ContainsKey(path))
                classLevel[path] = selection;
        }

        // The class-level answer costs milliseconds. The member walk costs a reference search per symbol, so it runs only
        // when the class-level answer is small enough for the refinement to matter and the walk to finish.
        var classes = classLevel.Values.OfType<TestSelection.Methods>().Sum(m => m.Patterns.Count);
        var allSameCode = walk.DroppedSameCode == walk.ChangedDeclarations;
        if (classes > RefineThreshold)
        {
            _workspace.Log($"test selection: {classes} test classes reachable; selecting at class level");
            return new SelectionResult(classLevel, walk.DroppedSameCode, allSameCode);
        }

        var outcome = await walk.RunAsync(cancellationToken).ConfigureAwait(false);
        if (!outcome.Completed)
        {
            _workspace.Log($"test selection: member walk over budget in {outcome.Milliseconds} ms, {outcome.Symbols} symbol(s), {classes} test class(es) reachable, {PatternsOf(classLevel)} pattern(s) selected; selecting at class level");
            return new SelectionResult(classLevel, walk.DroppedSameCode, allSameCode);
        }

        // A project the seeds selected whole reports the class-level answer's reason, so the summary gives the same
        // reason for it whichever answer is returned.
        var memberLevel = new Dictionary<RepoPath, TestSelection>(walk.Selections);
        foreach (var (path, _) in seededWhole)
            memberLevel[path] = classLevel[path];
        _workspace.Log($"test selection: member walk completed in {outcome.Milliseconds} ms, {outcome.Symbols} symbol(s), {classes} test class(es) reachable, {PatternsOf(memberLevel)} pattern(s) selected; selecting at member level");
        return new SelectionResult(memberLevel, walk.DroppedSameCode, allSameCode);
    }

    /// <summary>How many test name patterns the selections hold.</summary>
    private static int PatternsOf(IReadOnlyDictionary<RepoPath, TestSelection> selections) =>
        selections.Values.OfType<TestSelection.Methods>().Sum(m => m.Patterns.Count);
}
