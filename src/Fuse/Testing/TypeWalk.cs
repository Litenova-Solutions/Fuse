using Fuse.Paths;
using Fuse.Testing.Model;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Testing;

/// <summary>
///     The class-level answer: walks the <see cref="TypeGraph"/> backwards from the types that declare a changed
///     declaration and selects every test class it reaches. It binds nothing, so it answers in milliseconds on a
///     solution where the <see cref="MemberWalk"/> would run out of budget.
/// </summary>
/// <remarks>
///     A reached test file that declares no class selects its whole project. A reached application selects every test
///     project that depends on it (<see cref="HostRule"/>), because tests reach an application through its application host, not by
///     naming its types. When several reasons select one project whole, the last one reached is the one reported.
/// </remarks>
internal sealed class TypeWalk
{
    private readonly RepoWorkspace _workspace;

    // Each file's syntax facts by text version, kept across requests, so a rebuilt graph parses only the files that changed.
    private readonly Dictionary<DocumentId, (VersionStamp Version, TypeGraph.FileFacts Facts)> _facts = [];

    public TypeWalk(RepoWorkspace workspace) => _workspace = workspace;

    /// <summary>Selects the tests the types of <paramref name="seeds"/> reach, keyed by test project file.</summary>
    /// <param name="reachedProjects">The Roslyn projects of the changed projects and their dependents; the graph covers only these.</param>
    /// <param name="seeds">The changed declarations, each with its document, as <see cref="MemberWalk.SeedAsync"/> found them.</param>
    /// <param name="cancellationToken">Cancels building the graph.</param>
    public async Task<IReadOnlyDictionary<RepoPath, TestSelection>> SelectAsync(
        IReadOnlyList<Project> reachedProjects,
        IReadOnlyList<(Document Document, SyntaxNode Node)> seeds,
        CancellationToken cancellationToken)
    {
        var graph = _workspace.Graph;
        var typeGraph = await TypeGraph.BuildAsync(reachedProjects, p => p.FilePath is null ? null : graph.Find(_workspace.Root.PathOf(p.FilePath)), _facts, cancellationToken).ConfigureAwait(false);
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
                    selections.SelectWhole(entry.Node, "a test file without classes changed");
                else
                    selections.Add(entry.Node, entry.TestName + ".");
            }
            else if (entry.Node.IsExecutable)
            {
                foreach (var dependent in HostRule.DependentTestProjects(graph, entry.Node))
                    selections.SelectWhole(dependent, $"{entry.Node.Name} uses the changed code and runs behind an application host");
            }
        }

        return selections.Selections;
    }
}
