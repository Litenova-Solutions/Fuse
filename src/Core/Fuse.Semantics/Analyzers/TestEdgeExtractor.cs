using Fuse.Indexing;
using Microsoft.CodeAnalysis;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Emits <c>tests</c> edges from a test type to the source types it references (R5 part 2), the substrate M1
///     uses to select the tests that cover a changed symbol. Runs after the per-project analyzers have merged, so
///     it can link across projects (a test project references a production project) and resolve dependency
///     injection: a test that references an interface is also linked to the interface's registered
///     implementation through the <c>di_resolves_to</c> edges, so a test injecting <c>IOrderService</c> is
///     correctly linked to <c>OrderService</c>. This is what makes the covering set better than a plain
///     reference walk.
/// </summary>
/// <remarks>
///     Foreign-key safe: an edge is emitted only to a type whose node already exists in the merged graph (a
///     solution source type), never to a framework or third-party metadata type that has no node. Selection is
///     best-effort: a covering relationship reached only through reflection or a source generator is not seen, so
///     the produced set is sound (its edges are real) but not claimed complete. Granularity is the declaring type.
/// </remarks>
public sealed class TestEdgeExtractor
{
    private const double TestsWeight = 0.65;

    // Test-method attribute simple names across the common .NET test frameworks (xUnit, NUnit, MSTest).
    private static readonly HashSet<string> TestAttributeNames = new(StringComparer.Ordinal)
    {
        "FactAttribute", "TheoryAttribute", "TestAttribute", "TestMethodAttribute", "TestCaseAttribute",
    };

    /// <summary>
    ///     Extracts test edges over the loaded projects.
    /// </summary>
    /// <param name="projects">The loaded projects whose compilations to scan for test types.</param>
    /// <param name="existingNodeIds">The node ids already in the merged graph; edges link only to these (FK-safe).</param>
    /// <param name="diResolvesTo">Map of interface node id to its registered implementation node ids, from the DI edges.</param>
    /// <param name="rootDirectory">The workspace root, used to make node file paths relative.</param>
    /// <param name="cancellationToken">A token to cancel the scan.</param>
    /// <returns>The test-type nodes to add and the <c>tests</c> edges they anchor.</returns>
    public (IReadOnlyList<NodeRecord> Nodes, IReadOnlyList<SemanticEdgeRecord> Edges) Extract(
        IReadOnlyList<LoadedProject> projects,
        ISet<string> existingNodeIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>> diResolvesTo,
        string rootDirectory,
        CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edgeKeys = new HashSet<(string, string)>();
        var edges = new List<SemanticEdgeRecord>();

        foreach (var project in projects)
        {
            var compilation = project.Compilation;
            foreach (var type in SemanticNodes.EnumerateTypes(compilation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SemanticNodes.IsInSource(type, compilation) || !IsTestType(type))
                    continue;

                var fromId = SemanticNodes.TypeId(type);
                NodeRecord? fromNode = null;

                foreach (var referencedId in ReferencedTypeIds(type, compilation, cancellationToken))
                {
                    // Direct reference, plus the DI-resolved implementations when the referenced type is a
                    // registered interface. Every target must already be a node (FK-safe).
                    foreach (var targetId in Targets(referencedId, diResolvesTo))
                    {
                        if (targetId == fromId || !existingNodeIds.Contains(targetId) || !edgeKeys.Add((fromId, targetId)))
                            continue;

                        fromNode ??= AddTestNode(nodes, type, rootDirectory);
                        edges.Add(new SemanticEdgeRecord(
                            FromNodeId: fromId,
                            ToNodeId: targetId,
                            EdgeType: "tests",
                            Weight: TestsWeight,
                            Confidence: 1.0,
                            Evidence: $"{type.Name} covers {targetId}",
                            EvidenceFilePath: fromNode.FilePath));
                    }
                }
            }
        }

        return (nodes.Values.ToList(), edges);
    }

    // The target node ids for a referenced type: the type itself, plus its registered implementations when it is
    // an interface the DI graph resolves (so a test that names only the interface still covers the impl).
    private static IEnumerable<string> Targets(
        string referencedId, IReadOnlyDictionary<string, IReadOnlyList<string>> diResolvesTo)
    {
        yield return referencedId;
        if (diResolvesTo.TryGetValue(referencedId, out var impls))
            foreach (var impl in impls)
                yield return impl;
    }

    private static bool IsTestType(INamedTypeSymbol type) =>
        type.GetMembers().OfType<IMethodSymbol>().Any(m =>
            m.GetAttributes().Any(a => a.AttributeClass is { Name: { } name } && TestAttributeNames.Contains(name)));

    // The type ids a test type references, resolved through the semantic model over its own syntax. A member
    // reference counts as a reference to its containing type; only named types are returned.
    private static IEnumerable<string> ReferencedTypeIds(
        INamedTypeSymbol type, Compilation compilation, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var tree = syntaxRef.SyntaxTree;
            var model = compilation.GetSemanticModel(tree);
            var declaration = syntaxRef.GetSyntax(cancellationToken);
            foreach (var node in declaration.DescendantNodesAndSelf())
            {
                var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
                var referenced = symbol switch
                {
                    INamedTypeSymbol named => named,
                    { ContainingType: { } containing } => containing,
                    _ => null,
                };
                if (referenced is null)
                    continue;
                var id = SemanticNodes.TypeId(referenced.OriginalDefinition);
                if (seen.Add(id))
                    yield return id;
            }
        }
    }

    private static NodeRecord AddTestNode(Dictionary<string, NodeRecord> nodes, INamedTypeSymbol type, string root)
    {
        var node = SemanticNodes.TypeNode(type, root);
        nodes[node.NodeId] = node;
        return node;
    }
}
