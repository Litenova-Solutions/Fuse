using Fuse.Indexing;
using Microsoft.CodeAnalysis;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Discovers interface implementation and base-type inheritance edges between types declared in the
///     project's own source.
/// </summary>
/// <remarks>
///     Edges: <c>type -&gt; interface : implements</c> (weight 0.90) and <c>type -&gt; base : inherits</c>
///     (weight 0.75). Only directly-implemented interfaces and the immediate base type are emitted, and only
///     when the target is declared in source; external framework types (and <see cref="object" />) are
///     skipped. Reverse traversal (interface to implementations) is served by the store's incoming-edge query
///     rather than by separate reverse edges.
/// </remarks>
public sealed class InterfaceImplementationAnalyzer : ISemanticAnalyzer
{
    private const double ImplementsWeight = 0.90;
    private const double InheritsWeight = 0.75;

    /// <inheritdoc />
    public SemanticAnalyzerResult Analyze(SemanticAnalysisContext context, CancellationToken cancellationToken)
    {
        var compilation = context.Project.Compilation;
        var root = context.RootDirectory;
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edges = new List<SemanticEdgeRecord>();

        foreach (var type in SemanticNodes.EnumerateTypes(compilation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SemanticNodes.IsInSource(type, compilation))
                continue;

            var fromNode = AddNode(nodes, type, root);
            var evidencePath = fromNode.FilePath;

            foreach (var iface in type.Interfaces)
            {
                if (!SemanticNodes.IsInSource(iface, compilation))
                    continue;

                AddNode(nodes, iface, root);
                edges.Add(new SemanticEdgeRecord(
                    FromNodeId: SemanticNodes.TypeId(type),
                    ToNodeId: SemanticNodes.TypeId(iface),
                    EdgeType: "implements",
                    Weight: ImplementsWeight,
                    Confidence: 1.0,
                    Evidence: $"{type.Name} implements {iface.Name}",
                    EvidenceFilePath: evidencePath));
            }

            if (type.BaseType is { } baseType
                && baseType.SpecialType != SpecialType.System_Object
                && SemanticNodes.IsInSource(baseType, compilation))
            {
                AddNode(nodes, baseType, root);
                edges.Add(new SemanticEdgeRecord(
                    FromNodeId: SemanticNodes.TypeId(type),
                    ToNodeId: SemanticNodes.TypeId(baseType),
                    EdgeType: "inherits",
                    Weight: InheritsWeight,
                    Confidence: 1.0,
                    Evidence: $"{type.Name} inherits {baseType.Name}",
                    EvidenceFilePath: evidencePath));
            }
        }

        return SemanticAnalyzerResult.FromGraph(nodes.Values.ToList(), edges);
    }

    private static NodeRecord AddNode(Dictionary<string, NodeRecord> nodes, INamedTypeSymbol type, string root)
    {
        var node = SemanticNodes.TypeNode(type, root);
        nodes[node.NodeId] = node;
        return node;
    }
}
