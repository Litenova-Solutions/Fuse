using Fuse.Indexing;
using Microsoft.CodeAnalysis;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Discovers constructor-injection dependencies: which services a type asks for, and which concrete
///     implementations it therefore depends on.
/// </summary>
/// <remarks>
///     For each constructor parameter of a source-declared class, emits <c>consumer -&gt; service :
///     di_injects</c> (weight 0.75). When the injected service is registered in DI, also emits
///     <c>consumer -&gt; implementation : di_depends_on_impl</c> (weight 0.85), joining the injection to the
///     <c>di_resolves_to</c> mapping from <see cref="DiRegistrationAnalyzer" />. Only in-source parameter
///     types are considered, so framework dependencies do not flood the graph.
/// </remarks>
public sealed class ConstructorInjectionAnalyzer : ISemanticAnalyzer
{
    private const double InjectsWeight = 0.75;
    private const double DependsOnImplWeight = 0.85;

    // Options wrapper interfaces are configuration consumption, not service injection; the options analyzer
    // records them as options_consumes, so they are excluded here to keep di_injects to real services.
    private static readonly HashSet<string> OptionsWrappers =
        new(StringComparer.Ordinal) { "IOptions", "IOptionsMonitor", "IOptionsSnapshot" };

    private readonly DiRegistrationAnalyzer _diAnalyzer;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ConstructorInjectionAnalyzer" /> class.
    /// </summary>
    /// <param name="diAnalyzer">The DI analyzer used to resolve which implementation a service maps to.</param>
    public ConstructorInjectionAnalyzer(DiRegistrationAnalyzer diAnalyzer) => _diAnalyzer = diAnalyzer;

    /// <inheritdoc />
    public SemanticAnalyzerResult Analyze(SemanticAnalysisContext context, CancellationToken cancellationToken)
    {
        var compilation = context.Project.Compilation;
        var root = context.RootDirectory;

        // serviceNodeId -> implementation node, derived from the DI analyzer's di_resolves_to edges.
        var di = _diAnalyzer.Analyze(context, cancellationToken);
        var diNodesById = di.Nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        var serviceToImpl = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        foreach (var edge in di.Edges.Where(e => e.EdgeType == "di_resolves_to"))
        {
            if (diNodesById.TryGetValue(edge.ToNodeId, out var implNode))
                serviceToImpl[edge.FromNodeId] = implNode;
        }

        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edges = new List<SemanticEdgeRecord>();

        foreach (var type in SemanticNodes.EnumerateTypes(compilation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (type.TypeKind != TypeKind.Class || !SemanticNodes.IsInSource(type, compilation))
                continue;

            var consumerId = SemanticNodes.TypeId(type);
            var consumerNode = SemanticNodes.TypeNode(type, root);
            var evidencePath = consumerNode.FilePath;

            foreach (var constructor in type.InstanceConstructors)
            {
                if (constructor.IsImplicitlyDeclared)
                    continue;

                foreach (var parameter in constructor.Parameters)
                {
                    if (parameter.Type is not INamedTypeSymbol serviceType || !SemanticNodes.IsInSource(serviceType, compilation))
                        continue;
                    if (OptionsWrappers.Contains(serviceType.Name))
                        continue;

                    var serviceId = SemanticNodes.TypeId(serviceType);
                    nodes[consumerId] = consumerNode;
                    nodes[serviceId] = SemanticNodes.TypeNode(serviceType, root);
                    edges.Add(new SemanticEdgeRecord(
                        FromNodeId: consumerId,
                        ToNodeId: serviceId,
                        EdgeType: "di_injects",
                        Weight: InjectsWeight,
                        Confidence: 1.0,
                        Evidence: $"{type.Name}({serviceType.Name} {parameter.Name})",
                        EvidenceFilePath: evidencePath));

                    if (serviceToImpl.TryGetValue(serviceId, out var implNode) && implNode.NodeId != consumerId)
                    {
                        nodes[implNode.NodeId] = implNode;
                        edges.Add(new SemanticEdgeRecord(
                            FromNodeId: consumerId,
                            ToNodeId: implNode.NodeId,
                            EdgeType: "di_depends_on_impl",
                            Weight: DependsOnImplWeight,
                            Confidence: 0.9,
                            Evidence: $"{type.Name} depends on {implNode.DisplayName} via {serviceType.Name}",
                            EvidenceFilePath: evidencePath));
                    }
                }
            }
        }

        return SemanticAnalyzerResult.FromGraph(nodes.Values.ToList(), edges);
    }
}
