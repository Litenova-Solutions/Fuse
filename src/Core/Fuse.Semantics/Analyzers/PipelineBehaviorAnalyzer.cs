using Fuse.Indexing;
using Microsoft.CodeAnalysis;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Discovers MediatR pipeline behaviors: every in-source type that implements
///     <c>IPipelineBehavior&lt;TRequest, TResponse&gt;</c> wraps request handling in the mediator pipeline.
/// </summary>
/// <remarks>
///     Emits <c>service:IPipelineBehavior -&gt; behavior : pipeline_behavior</c> (weight 0.90) so a resolver can
///     answer "what runs around every request". Behaviors are registered as open generics
///     (<c>AddOpenBehavior(typeof(LoggingBehavior&lt;,&gt;))</c>), so the binding is by interface, not a call the
///     change tracker can follow.
/// </remarks>
public sealed class PipelineBehaviorAnalyzer : ISemanticAnalyzer
{
    private const double BehaviorWeight = 0.90;
    private const string BehaviorInterface = "IPipelineBehavior";

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
            if (!type.AllInterfaces.Any(i => i.Name == BehaviorInterface))
                continue;

            var behaviorNode = SemanticNodes.TypeNode(type, root);
            var pipelineNode = SyntheticNodes.Service(BehaviorInterface);
            nodes[behaviorNode.NodeId] = behaviorNode;
            nodes[pipelineNode.NodeId] = pipelineNode;
            edges.Add(new SemanticEdgeRecord(
                FromNodeId: pipelineNode.NodeId,
                ToNodeId: behaviorNode.NodeId,
                EdgeType: "pipeline_behavior",
                Weight: BehaviorWeight,
                Confidence: 0.9,
                Evidence: $"{type.Name} is a MediatR pipeline behavior",
                EvidenceFilePath: behaviorNode.FilePath));
        }

        return SemanticAnalyzerResult.FromGraph(nodes.Values.ToList(), edges);
    }
}
