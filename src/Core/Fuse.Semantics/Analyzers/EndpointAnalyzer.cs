using Fuse.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Discovers minimal-API endpoints, gRPC services, and SignalR hubs mapped on the application builder.
/// </summary>
/// <remarks>
///     For <c>MapGet</c>/<c>MapPost</c>/<c>MapPut</c>/<c>MapDelete</c> with a method-group handler, emits
///     <c>route -&gt; handler method : route_handles</c>. For <c>MapGrpcService&lt;T&gt;()</c> emits
///     <c>service:gRPC -&gt; T : grpc_endpoint</c>, and for <c>MapHub&lt;T&gt;()</c> emits
///     <c>service:SignalR -&gt; T : signalr_endpoint</c>. These are registration calls whose target is a type
///     argument or a method reference, which a lexical or tree-sitter index cannot resolve to the handler.
/// </remarks>
public sealed class EndpointAnalyzer : ISemanticAnalyzer
{
    private const double RouteHandlesWeight = 1.00;
    private const double EndpointWeight = 0.95;

    private static readonly Dictionary<string, string> VerbByMap = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH",
    };

    /// <inheritdoc />
    public SemanticAnalyzerResult Analyze(SemanticAnalysisContext context, CancellationToken cancellationToken)
    {
        var compilation = context.Project.Compilation;
        var root = context.RootDirectory;
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edges = new List<SemanticEdgeRecord>();
        var routes = new List<RouteRecord>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            var filePath = NormalizeRelative(root, tree.FilePath);

            foreach (var invocation in tree.GetRoot(cancellationToken).DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member)
                    continue;
                var name = member.Name;
                var methodName = name.Identifier.ValueText;

                if (VerbByMap.TryGetValue(methodName, out var verb))
                    CollectMinimalApi(model, invocation, verb, root, filePath, nodes, edges, routes, cancellationToken);
                else if (methodName is "MapGrpcService" && name is GenericNameSyntax grpc)
                    CollectTypedEndpoint(model, grpc, "gRPC", "grpc_endpoint", root, filePath, nodes, edges, cancellationToken);
                else if (methodName is "MapHub" && name is GenericNameSyntax hub)
                    CollectTypedEndpoint(model, hub, "SignalR", "signalr_endpoint", root, filePath, nodes, edges, cancellationToken);
            }
        }

        return new SemanticAnalyzerResult(nodes.Values.ToList(), edges, routes, [], [], []);
    }

    private static void CollectMinimalApi(
        SemanticModel model, InvocationExpressionSyntax invocation, string verb, string root, string filePath,
        Dictionary<string, NodeRecord> nodes, List<SemanticEdgeRecord> edges, List<RouteRecord> routes, CancellationToken cancellationToken)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 2 || args[0].Expression is not LiteralExpressionSyntax { Token.ValueText: { } rawPattern })
            return;
        // The handler must be a method reference (a method group), not a lambda, to resolve to a symbol. A
        // method group in a delegate-conversion position can land in CandidateSymbols rather than Symbol.
        var handlerInfo = model.GetSymbolInfo(args[1].Expression, cancellationToken);
        var handler = handlerInfo.Symbol as IMethodSymbol
                      ?? handlerInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (handler is null)
            return;

        var pattern = NormalizePattern(rawPattern);
        var routeNodeId = SemanticNodes.RouteId(verb, pattern);
        var span = invocation.GetLocation().GetLineSpan();
        var startLine = span.StartLinePosition.Line + 1;
        var endLine = span.EndLinePosition.Line + 1;
        var methodNode = SemanticNodes.MethodNode(handler, root);
        nodes[methodNode.NodeId] = methodNode;
        nodes[routeNodeId] = new NodeRecord(routeNodeId, "route", $"{verb} {pattern}", routeNodeId, filePath, StartLine: startLine, EndLine: endLine, Signature: $"{verb} {pattern}");
        edges.Add(new SemanticEdgeRecord(
            FromNodeId: routeNodeId,
            ToNodeId: methodNode.NodeId,
            EdgeType: "route_handles",
            Weight: RouteHandlesWeight,
            Confidence: 1.0,
            Evidence: $"{verb} {pattern} -> {handler.ContainingType?.Name}.{handler.Name}",
            EvidenceFilePath: filePath,
            EvidenceStartLine: startLine,
            EvidenceEndLine: endLine));
        routes.Add(new RouteRecord(routeNodeId, verb, pattern, filePath, startLine, endLine, "minimal-api", SymbolIdBuilder.Build(handler)));
    }

    private static void CollectTypedEndpoint(
        SemanticModel model, GenericNameSyntax generic, string contract, string edgeType, string root, string filePath,
        Dictionary<string, NodeRecord> nodes, List<SemanticEdgeRecord> edges, CancellationToken cancellationToken)
    {
        if (generic.TypeArgumentList.Arguments.Count != 1)
            return;
        if (model.GetTypeInfo(generic.TypeArgumentList.Arguments[0], cancellationToken).Type is not INamedTypeSymbol target)
            return;

        var targetNode = SemanticNodes.TypeNode(target, root);
        var contractNode = SyntheticNodes.Service(contract);
        nodes[targetNode.NodeId] = targetNode;
        nodes[contractNode.NodeId] = contractNode;
        edges.Add(new SemanticEdgeRecord(
            FromNodeId: contractNode.NodeId,
            ToNodeId: targetNode.NodeId,
            EdgeType: edgeType,
            Weight: EndpointWeight,
            Confidence: 0.95,
            Evidence: $"{contract} endpoint {target.Name}",
            EvidenceFilePath: filePath));
    }

    private static string NormalizePattern(string pattern)
    {
        var trimmed = pattern.Trim();
        if (trimmed.Length == 0)
            return "/";
        if (!trimmed.StartsWith('/'))
            trimmed = "/" + trimmed;
        return trimmed.Length > 1 ? trimmed.TrimEnd('/') : trimmed;
    }

    private static string NormalizeRelative(string rootDirectory, string absolutePath) =>
        string.IsNullOrEmpty(absolutePath)
            ? absolutePath
            : Path.GetRelativePath(rootDirectory, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
}
