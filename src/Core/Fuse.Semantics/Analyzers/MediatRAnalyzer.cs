using Fuse.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Discovers MediatR request/handler wiring: which handler processes a request or notification, and where
///     requests are sent.
/// </summary>
/// <remarks>
///     For each source type implementing <c>IRequestHandler&lt;TRequest, TResponse&gt;</c>,
///     <c>IRequestHandler&lt;TRequest&gt;</c>, or <c>INotificationHandler&lt;TNotification&gt;</c>, emits
///     <c>request -&gt; handler : mediatr_handles</c> (weight 0.95). For each <c>Send</c>/<c>Publish</c> call
///     whose argument is a request or notification type, emits <c>caller -&gt; request : sends_request</c>
///     (weight 0.70). Interfaces are matched by simple name so the real MediatR package and a local
///     equivalent are both recognized.
/// </remarks>
public sealed class MediatRAnalyzer : ISemanticAnalyzer
{
    private const double HandlesWeight = 0.95;
    private const double SendsWeight = 0.70;

    private static readonly HashSet<string> HandlerInterfaces =
        new(StringComparer.Ordinal) { "IRequestHandler", "INotificationHandler" };

    private static readonly HashSet<string> MessageInterfaces =
        new(StringComparer.Ordinal) { "IRequest", "INotification" };

    private static readonly HashSet<string> SendMethods =
        new(StringComparer.Ordinal) { "Send", "Publish", "SendAsync", "PublishAsync" };

    /// <inheritdoc />
    public SemanticAnalyzerResult Analyze(SemanticAnalysisContext context, CancellationToken cancellationToken)
    {
        var compilation = context.Project.Compilation;
        var root = context.RootDirectory;
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edges = new List<SemanticEdgeRecord>();

        CollectHandlerEdges(compilation, root, nodes, edges, cancellationToken);
        CollectSendEdges(compilation, root, nodes, edges, cancellationToken);

        return SemanticAnalyzerResult.FromGraph(nodes.Values.ToList(), edges);
    }

    private static void CollectHandlerEdges(
        Compilation compilation,
        string root,
        Dictionary<string, NodeRecord> nodes,
        List<SemanticEdgeRecord> edges,
        CancellationToken cancellationToken)
    {
        foreach (var type in SemanticNodes.EnumerateTypes(compilation))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (type.TypeKind != TypeKind.Class || !SemanticNodes.IsInSource(type, compilation))
                continue;

            foreach (var handlerInterface in type.AllInterfaces)
            {
                if (!HandlerInterfaces.Contains(handlerInterface.Name) || handlerInterface.TypeArguments.Length == 0)
                    continue;
                if (handlerInterface.TypeArguments[0] is not INamedTypeSymbol request)
                    continue;

                var handlerNode = SemanticNodes.TypeNode(type, root);
                var requestNode = SemanticNodes.TypeNode(request, root);
                nodes[handlerNode.NodeId] = handlerNode;
                nodes[requestNode.NodeId] = requestNode;
                edges.Add(new SemanticEdgeRecord(
                    FromNodeId: requestNode.NodeId,
                    ToNodeId: handlerNode.NodeId,
                    EdgeType: "mediatr_handles",
                    Weight: HandlesWeight,
                    Confidence: 0.95,
                    Evidence: $"{type.Name} handles {request.Name}",
                    EvidenceFilePath: handlerNode.FilePath));
            }
        }
    }

    private static void CollectSendEdges(
        Compilation compilation,
        string root,
        Dictionary<string, NodeRecord> nodes,
        List<SemanticEdgeRecord> edges,
        CancellationToken cancellationToken)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);

            foreach (var invocation in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member
                    || !SendMethods.Contains(member.Name.Identifier.ValueText)
                    || invocation.ArgumentList.Arguments.Count == 0)
                {
                    continue;
                }

                var argument = invocation.ArgumentList.Arguments[0].Expression;
                if (model.GetTypeInfo(argument, cancellationToken).Type is not INamedTypeSymbol request)
                    continue;
                if (!IsMessage(request) || !SemanticNodes.IsInSource(request, compilation))
                    continue;

                var caller = EnclosingType(model, invocation, cancellationToken);
                if (caller is null)
                    continue;

                var callerNode = SemanticNodes.TypeNode(caller, root);
                var requestNode = SemanticNodes.TypeNode(request, root);
                nodes[callerNode.NodeId] = callerNode;
                nodes[requestNode.NodeId] = requestNode;
                edges.Add(new SemanticEdgeRecord(
                    FromNodeId: callerNode.NodeId,
                    ToNodeId: requestNode.NodeId,
                    EdgeType: "sends_request",
                    Weight: SendsWeight,
                    Confidence: 0.8,
                    Evidence: $"{caller.Name} sends {request.Name}",
                    EvidenceFilePath: SemanticNodes.TypeNode(caller, root).FilePath));
            }
        }
    }

    private static bool IsMessage(INamedTypeSymbol type) =>
        type.AllInterfaces.Any(i => MessageInterfaces.Contains(i.Name));

    private static INamedTypeSymbol? EnclosingType(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        var typeDeclaration = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return typeDeclaration is null ? null : model.GetDeclaredSymbol(typeDeclaration, cancellationToken) as INamedTypeSymbol;
    }
}
