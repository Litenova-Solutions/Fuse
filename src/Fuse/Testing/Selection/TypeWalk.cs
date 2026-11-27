using System.Collections.Concurrent;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Testing.Caching;
using Fuse.Testing.Model;
using Fuse.Workspace.Generations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace Fuse.Testing.Selection;

/// <summary>
///     The class-level answer: walks the <see cref="TypeGraph"/> backwards from the types that declare a changed
///     declaration and selects every test class it reaches. It binds nothing, so it answers in milliseconds on a
///     solution where the <see cref="MemberWalk"/> would run out of budget.
/// </summary>
/// <remarks>
///     A reached test file that declares no class selects its whole project. A reached application selects every test
///     project that depends on it (<see cref="HostRule"/>), because tests reach an application through its application host, not by
///     naming its types. When several reasons select one project whole, the last one reached is the one reported.
///     The facts kept between selections live in a concurrent dictionary keyed by document id and text version, so
///     selections that run at the same time share them without corrupting them.
/// </remarks>
internal sealed class TypeWalk
{
    /// <summary>Maximum number of document syntax-fact entries retained across concurrent slices.</summary>
    internal const int DefaultMaxFactEntries = 4096;

    private readonly RepoWorkspace _workspace;
    private readonly DynamicTestMap? _dynamicMap;
    private readonly int _maxFactEntries;

    // Each file's syntax facts by text version, kept across requests in a bounded LRU cache so concurrent workers
    // editing disjoint slices share cached facts without thrashing each other.
    private readonly ConcurrentDictionary<DocumentId, (VersionStamp Version, TypeGraph.FileFacts Facts)> _facts = new();
    private readonly Lock _lruGate = new();
    private readonly LinkedList<DocumentId> _lruOrder = new();
    private readonly Dictionary<DocumentId, LinkedListNode<DocumentId>> _lruNodes = new();

    public TypeWalk(RepoWorkspace workspace)
        : this(workspace, null, DefaultMaxFactEntries)
    {
    }

    internal TypeWalk(RepoWorkspace workspace, DynamicTestMap? dynamicMap, int maxFactEntries = DefaultMaxFactEntries)
    {
        _workspace = workspace;
        _dynamicMap = dynamicMap;
        _maxFactEntries = Math.Max(1, maxFactEntries);
    }

    /// <summary>How many files' facts are kept.</summary>
    public int KeptFacts => _facts.Count;

    /// <summary>Selects the tests the types of <paramref name="seeds"/> reach, keyed by test project file.</summary>
    /// <param name="reachedProjects">The Roslyn projects of the changed projects and their dependents; the graph covers only these.</param>
    /// <param name="seeds">The changed declarations, each with its document, as <see cref="MemberWalk.SeedAsync"/> found them.</param>
    /// <param name="cancellationToken">Cancels building the graph.</param>
    public Task<IReadOnlyDictionary<RepoPath, TestSelection>> SelectAsync(
        IReadOnlyList<Project> reachedProjects,
        IReadOnlyList<(Document Document, SyntaxNode Node)> seeds,
        CancellationToken cancellationToken) =>
        SelectAsync(reachedProjects, seeds, _workspace.Graph, _workspace.Root, cancellationToken);

    /// <summary>Selects the tests the types of <paramref name="seeds"/> reach, keyed by test project file, reading <paramref name="graph"/>.</summary>
    /// <param name="reachedProjects">The Roslyn projects of the changed projects and their dependents; the graph covers only these.</param>
    /// <param name="seeds">The changed declarations, each with its document, as <see cref="MemberWalk.SeedAsync"/> found them.</param>
    /// <param name="graph">The project graph of the generation the selection reads.</param>
    /// <param name="root">Turns the paths Roslyn reports into repository paths.</param>
    /// <param name="cancellationToken">Cancels building the graph.</param>
    public async Task<IReadOnlyDictionary<RepoPath, TestSelection>> SelectAsync(
        IReadOnlyList<Project> reachedProjects,
        IReadOnlyList<(Document Document, SyntaxNode Node)> seeds,
        RepoGraph graph,
        RepoRoot root,
        CancellationToken cancellationToken)
    {
        // Only drop document IDs that belong to the reached projects and were replaced by a reload; keep facts from
        // other projects in the bounded LRU cache so concurrent slices do not evict each other.
        var reachedProjectIds = reachedProjects.Select(p => p.Id).ToHashSet();
        var live = reachedProjects.SelectMany(p => p.Documents).Select(d => d.Id).ToHashSet();
        var stale = _facts.Keys.Where(id => reachedProjectIds.Contains(id.ProjectId) && !live.Contains(id)).ToList();
        if (stale.Count > 0)
        {
            foreach (var id in stale)
                _facts.TryRemove(id, out _);
            lock (_lruGate)
            {
                foreach (var id in stale)
                    RemoveLruLocked(id);
            }
        }

        var typeGraph = await TypeGraph.BuildAsync(reachedProjects, p => p.FilePath is null ? null : graph.Find(root.PathOf(p.FilePath)), _facts, cancellationToken).ConfigureAwait(false);
        TouchAndTrimLru(live);
        var start = seeds
            .Select(s => new TypeGraph.TypeKey(s.Document.Project.Id, TypeGraph.DeclaringName(s.Node, s.Document.FilePath ?? s.Document.Name)))
            .Where(typeGraph.Types.ContainsKey)
            .ToList();
        var selections = new SelectionBuilder();
        foreach (var key in typeGraph.ReverseClosure(start))
        {
            var entry = typeGraph.Types[key];
            if (entry.Node is null)
                continue;
            if (entry.Node.IsTest)
            {
                if (TypeGraph.IsFileKey(key.Name))
                {
                    // A file the build wrote carries no selection: tests do not live in build output.
                    if (!PathRules.IsBuildOutput(root.Path, TypeGraph.FilePathOfKey(key.Name)))
                        selections.SelectWhole(entry.Node, "a test file without classes changed");
                }
                else
                {
                    selections.Add(entry.Node, entry.TestName + ".");
                }
            }
            else if (entry.Node.IsExecutable)
            {
                // Recorded coverage only adds tests to the selection; the static whole-project selection below
                // stands, so a test the static walks selected is never dropped.
                if (_dynamicMap is { HasAnyMapping: true })
                    TrySelectExecutableFromDynamicMap(entry.Node, key.Name, seeds, graph, root, selections);

                foreach (var dependent in HostRule.DependentTestProjects(graph, entry.Node))
                    selections.SelectWhole(dependent, $"{entry.Node.Name} uses the changed code and runs behind an application host");
            }
        }

        return selections.Selections;
    }

    private bool TrySelectExecutableFromDynamicMap(
        ProjectNode executable,
        string typeKey,
        IReadOnlyList<(Document Document, SyntaxNode Node)> seeds,
        RepoGraph graph,
        RepoRoot root,
        SelectionBuilder selections)
    {
        if (_dynamicMap is null)
            return false;

        string? sourceRelative = null;
        foreach (var seed in seeds)
        {
            // Newly added declarations or un-diffed file statements require static HostRule fallback.
            if (seed.Node is CompilationUnitSyntax)
                return false;
            if (sourceRelative is null
                && seed.Document.Project.FilePath is not null
                && root.PathOf(seed.Document.Project.FilePath) == executable.Path
                && seed.Document.FilePath is not null)
            {
                sourceRelative = root.PathOf(seed.Document.FilePath).Relative;
            }
        }

        if (!_dynamicMap.TryResolveForType(typeKey, sourceRelative, root, out var resolved))
            return false;

        return HostRule.ApplyDynamicSelections(graph, root, resolved, selections);
    }

    private void TouchAndTrimLru(HashSet<DocumentId> live)
    {
        lock (_lruGate)
        {
            foreach (var id in live)
            {
                if (!_facts.ContainsKey(id))
                    continue;
                if (_lruNodes.TryGetValue(id, out var existing))
                {
                    _lruOrder.Remove(existing);
                    _lruOrder.AddLast(existing);
                }
                else
                {
                    _lruNodes[id] = _lruOrder.AddLast(id);
                }
            }

            while (_facts.Count > _maxFactEntries && _lruOrder.First is { } oldest)
            {
                _lruOrder.RemoveFirst();
                _lruNodes.Remove(oldest.Value);
                _facts.TryRemove(oldest.Value, out _);
            }
        }
    }

    private void RemoveLruLocked(DocumentId id)
    {
        if (_lruNodes.Remove(id, out var node))
            _lruOrder.Remove(node);
    }
}
