using Fuse.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Discovers ASP.NET MVC controller routes and links each to the action method that handles it.
/// </summary>
/// <remarks>
///     For each controller action carrying an HTTP verb attribute, builds a route node
///     (<c>route:{METHOD}:{pattern}</c>), resolves the action method symbol, and emits
///     <c>route -&gt; handler method : route_handles</c> (weight 1.00) along with a
///     <see cref="RouteRecord" /> whose handler symbol id is set. The controller-level <c>[Route]</c> prefix
///     is combined with the action template; a verb attribute with no template falls back to the action name.
///     Minimal-API routes are handled by the syntax route extractor; this analyzer focuses on the MVC
///     route-to-method edge that resolve and review traverse.
/// </remarks>
public sealed class AspNetRouteAnalyzer : ISemanticAnalyzer
{
    private const double RouteHandlesWeight = 1.00;

    private static readonly Dictionary<string, string> VerbByAttribute = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET",
        ["HttpPost"] = "POST",
        ["HttpPut"] = "PUT",
        ["HttpDelete"] = "DELETE",
        ["HttpPatch"] = "PATCH",
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

            foreach (var controller in tree.GetRoot(cancellationToken).DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var prefix = ControllerRoutePrefix(controller);
                foreach (var method in controller.Members.OfType<MethodDeclarationSyntax>())
                {
                    foreach (var attribute in EnumerateAttributes(method.AttributeLists))
                    {
                        if (!VerbByAttribute.TryGetValue(SimpleName(attribute), out var verb))
                            continue;

                        var template = FirstStringArgument(attribute) ?? method.Identifier.ValueText;
                        var pattern = NormalizePattern(Combine(prefix, template));
                        var span = method.GetLocation().GetLineSpan();
                        var startLine = span.StartLinePosition.Line + 1;
                        var endLine = span.EndLinePosition.Line + 1;
                        var routeNodeId = SemanticNodes.RouteId(verb, pattern);

                        string? handlerSymbolId = null;
                        if (model.GetDeclaredSymbol(method, cancellationToken) is IMethodSymbol handler)
                        {
                            var methodNode = SemanticNodes.MethodNode(handler, root);
                            nodes[methodNode.NodeId] = methodNode;
                            nodes[routeNodeId] = RouteNode(routeNodeId, verb, pattern, filePath, startLine, endLine);
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
                            handlerSymbolId = SymbolIdBuilder.Build(handler);
                        }

                        routes.Add(new RouteRecord(
                            RouteId: routeNodeId,
                            HttpMethod: verb,
                            RoutePattern: pattern,
                            FilePath: filePath,
                            StartLine: startLine,
                            EndLine: endLine,
                            SourceKind: "mvc",
                            HandlerSymbolId: handlerSymbolId));
                    }
                }
            }
        }

        return new SemanticAnalyzerResult(nodes.Values.ToList(), edges, routes, [], [], []);
    }

    private static NodeRecord RouteNode(string nodeId, string verb, string pattern, string filePath, int startLine, int endLine) =>
        new(
            NodeId: nodeId,
            Kind: "route",
            DisplayName: $"{verb} {pattern}",
            StableKey: nodeId,
            FilePath: filePath,
            StartLine: startLine,
            EndLine: endLine,
            Signature: $"{verb} {pattern}");

    private static string ControllerRoutePrefix(ClassDeclarationSyntax controller)
    {
        foreach (var attribute in EnumerateAttributes(controller.AttributeLists))
        {
            if (SimpleName(attribute) == "Route" && FirstStringArgument(attribute) is { } prefix)
                return prefix.TrimEnd('/');
        }

        return string.Empty;
    }

    private static IEnumerable<AttributeSyntax> EnumerateAttributes(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var list in lists)
            foreach (var attribute in list.Attributes)
                yield return attribute;
    }

    private static string SimpleName(AttributeSyntax attribute) => attribute.Name.ToString().Split('.').Last();

    private static string? FirstStringArgument(AttributeSyntax attribute)
    {
        if (attribute.ArgumentList is null)
            return null;
        foreach (var argument in attribute.ArgumentList.Arguments)
        {
            if (argument.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                return literal.Token.ValueText;
        }

        return null;
    }

    private static string Combine(string prefix, string template)
    {
        if (string.IsNullOrEmpty(prefix))
            return template;
        if (string.IsNullOrEmpty(template))
            return prefix;
        return $"{prefix.TrimEnd('/')}/{template.TrimStart('/')}";
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
