using Fuse.Indexing;
using Microsoft.CodeAnalysis;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Emits type-level <c>references</c> edges: a source type that uses a member or the type of another source
///     type gets an edge to it. This is the persisted reference substrate (R5) that <c>fuse_impact</c> reads to
///     answer "what references this symbol" without a live <c>SymbolFinder</c> pass at query time.
/// </summary>
/// <remarks>
///     Granularity is the declaring type, not the individual member, to bound row volume (finding 7's kill-risk):
///     one edge per (referencing type, referenced type) pair, deduped, rather than one per reference site. The
///     referenced type is resolved through the semantic model over the referencing type's own syntax, so only
///     real, bound references count; unresolved or framework symbols are skipped. A type never references itself.
///     Reverse traversal (who references T) is the store's incoming-edge query, as with the other analyzers.
/// </remarks>
public sealed class ReferenceEdgeAnalyzer : ISemanticAnalyzer
{
    // Matches EdgeWeightProvider's "references" weight (the weakest structural edge): a reference is real signal
    // but far weaker than a wiring edge, so it tunes ranking and seeds impact without dominating.
    private const double ReferencesWeight = 0.15;

    /// <inheritdoc />
    public SemanticAnalyzerResult Analyze(SemanticAnalysisContext context, CancellationToken cancellationToken)
    {
        var compilation = context.Project.Compilation;
        var root = context.RootDirectory;
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edgeKeys = new HashSet<(string From, string To)>();
        var edges = new List<SemanticEdgeRecord>();

        foreach (var type in SemanticNodes.EnumerateTypes(compilation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SemanticNodes.IsInSource(type, compilation))
                continue;

            var fromId = SemanticNodes.TypeId(type);
            NodeRecord? fromNode = null;

            foreach (var syntaxRef in type.DeclaringSyntaxReferences)
            {
                var tree = syntaxRef.SyntaxTree;
                var model = compilation.GetSemanticModel(tree);
                var declaration = syntaxRef.GetSyntax(cancellationToken);
                foreach (var node in declaration.DescendantNodesAndSelf())
                {
                    var referenced = ResolveReferencedType(model, node, cancellationToken);
                    if (referenced is null || !SemanticNodes.IsInSource(referenced, compilation))
                        continue;

                    // A type referencing itself, or the same (from, to) pair twice, adds no edge.
                    var referencedType = referenced.OriginalDefinition;
                    if (SymbolEqualityComparer.Default.Equals(referencedType, type.OriginalDefinition))
                        continue;
                    var toId = SemanticNodes.TypeId(referencedType);
                    if (fromId == toId || !edgeKeys.Add((fromId, toId)))
                        continue;

                    fromNode ??= AddNode(nodes, type, root);
                    AddNode(nodes, referencedType, root);
                    edges.Add(new SemanticEdgeRecord(
                        FromNodeId: fromId,
                        ToNodeId: toId,
                        EdgeType: "references",
                        Weight: ReferencesWeight,
                        Confidence: 1.0,
                        Evidence: $"{type.Name} references {referencedType.Name}",
                        EvidenceFilePath: fromNode.FilePath));
                }
            }
        }

        return SemanticAnalyzerResult.FromGraph(nodes.Values.ToList(), edges);
    }

    // Resolves the named type a syntax node references, if any: the symbol it binds to, mapped to the type that
    // declares it (a member reference counts as a reference to its containing type). Returns null for unbound
    // nodes, non-type/non-member symbols, and references that do not resolve to a named type.
    private static INamedTypeSymbol? ResolveReferencedType(
        SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
        if (symbol is null)
            return null;

        return symbol switch
        {
            INamedTypeSymbol named => named,
            { ContainingType: { } containing } => containing,
            _ => null,
        };
    }

    private static NodeRecord AddNode(Dictionary<string, NodeRecord> nodes, INamedTypeSymbol type, string root)
    {
        var node = SemanticNodes.TypeNode(type, root);
        nodes[node.NodeId] = node;
        return node;
    }
}
