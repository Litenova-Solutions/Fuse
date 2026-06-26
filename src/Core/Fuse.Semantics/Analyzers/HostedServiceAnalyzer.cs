using Fuse.Indexing;
using Microsoft.CodeAnalysis;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Discovers background services: every in-source type that implements <c>IHostedService</c> (directly or
///     through <c>BackgroundService</c>) is a worker the host runs.
/// </summary>
/// <remarks>
///     Emits <c>service:IHostedService -&gt; worker : hosted_service</c> (weight 0.90) so a resolver can answer
///     "what runs in the background". Matching is by interface simple name, so the framework
///     <c>Microsoft.Extensions.Hosting.IHostedService</c> and a local equivalent are both recognized. A
///     tree-sitter graph cannot follow this because the worker is tied to the host only through the type
///     hierarchy, not a call.
/// </remarks>
public sealed class HostedServiceAnalyzer : ISemanticAnalyzer
{
    private const double HostedWeight = 0.90;
    private const string HostedServiceInterface = "IHostedService";

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
            if (type.TypeKind != TypeKind.Class || type.IsAbstract || !SemanticNodes.IsInSource(type, compilation))
                continue;
            if (!type.AllInterfaces.Any(i => i.Name == HostedServiceInterface))
                continue;

            var workerNode = SemanticNodes.TypeNode(type, root);
            var hostNode = SyntheticNodes.Service(HostedServiceInterface);
            nodes[workerNode.NodeId] = workerNode;
            nodes[hostNode.NodeId] = hostNode;
            edges.Add(new SemanticEdgeRecord(
                FromNodeId: hostNode.NodeId,
                ToNodeId: workerNode.NodeId,
                EdgeType: "hosted_service",
                Weight: HostedWeight,
                Confidence: 0.9,
                Evidence: $"{type.Name} is a hosted background service",
                EvidenceFilePath: workerNode.FilePath));
        }

        return SemanticAnalyzerResult.FromGraph(nodes.Values.ToList(), edges);
    }
}
