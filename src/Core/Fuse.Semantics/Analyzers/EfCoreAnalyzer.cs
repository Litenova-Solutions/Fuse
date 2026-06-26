using Fuse.Indexing;
using Microsoft.CodeAnalysis;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Discovers Entity Framework Core wiring: which entities a <c>DbContext</c> exposes, and which
///     configuration class shapes each entity.
/// </summary>
/// <remarks>
///     For each in-source class deriving from <c>DbContext</c>, every <c>DbSet&lt;TEntity&gt;</c> property emits
///     <c>context -&gt; entity : ef_entity</c> (weight 0.90). For each class implementing
///     <c>IEntityTypeConfiguration&lt;TEntity&gt;</c>, emits <c>entity -&gt; configuration : ef_configures</c>
///     (weight 0.90). The entity-to-configuration link is by generic interface argument, which a lexical or
///     tree-sitter index cannot resolve.
/// </remarks>
public sealed class EfCoreAnalyzer : ISemanticAnalyzer
{
    private const double Weight = 0.90;
    private const string DbContextBase = "DbContext";
    private const string DbSetType = "DbSet";
    private const string ConfigurationInterface = "IEntityTypeConfiguration";

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
            if (type.TypeKind != TypeKind.Class || !SemanticNodes.IsInSource(type, compilation))
                continue;

            if (DerivesFrom(type, DbContextBase))
                CollectDbSets(type, root, nodes, edges);
            CollectConfigurations(type, root, nodes, edges);
        }

        return SemanticAnalyzerResult.FromGraph(nodes.Values.ToList(), edges);
    }

    private static void CollectDbSets(
        INamedTypeSymbol context, string root, Dictionary<string, NodeRecord> nodes, List<SemanticEdgeRecord> edges)
    {
        foreach (var property in context.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.Type is not INamedTypeSymbol { Name: DbSetType } dbSet || dbSet.TypeArguments.Length != 1)
                continue;
            if (dbSet.TypeArguments[0] is not INamedTypeSymbol entity)
                continue;

            var contextNode = SemanticNodes.TypeNode(context, root);
            var entityNode = SemanticNodes.TypeNode(entity, root);
            nodes[contextNode.NodeId] = contextNode;
            nodes[entityNode.NodeId] = entityNode;
            edges.Add(new SemanticEdgeRecord(
                FromNodeId: contextNode.NodeId,
                ToNodeId: entityNode.NodeId,
                EdgeType: "ef_entity",
                Weight: Weight,
                Confidence: 0.9,
                Evidence: $"{context.Name} exposes DbSet<{entity.Name}>",
                EvidenceFilePath: contextNode.FilePath));
        }
    }

    private static void CollectConfigurations(
        INamedTypeSymbol type, string root, Dictionary<string, NodeRecord> nodes, List<SemanticEdgeRecord> edges)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.Name != ConfigurationInterface || iface.TypeArguments.Length != 1)
                continue;
            if (iface.TypeArguments[0] is not INamedTypeSymbol entity)
                continue;

            var entityNode = SemanticNodes.TypeNode(entity, root);
            var configNode = SemanticNodes.TypeNode(type, root);
            nodes[entityNode.NodeId] = entityNode;
            nodes[configNode.NodeId] = configNode;
            edges.Add(new SemanticEdgeRecord(
                FromNodeId: entityNode.NodeId,
                ToNodeId: configNode.NodeId,
                EdgeType: "ef_configures",
                Weight: Weight,
                Confidence: 0.9,
                Evidence: $"{type.Name} configures {entity.Name}",
                EvidenceFilePath: configNode.FilePath));
        }
    }

    private static bool DerivesFrom(INamedTypeSymbol type, string baseName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.Name == baseName)
                return true;
        return false;
    }
}
