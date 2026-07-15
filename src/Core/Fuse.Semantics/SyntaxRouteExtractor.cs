using Fuse.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Semantics;

/// <summary>
///     Extracts HTTP routes from ASP.NET controller actions and minimal-API registrations in a C# file as
///     <see cref="RouteRecord" />s, using syntax analysis only.
/// </summary>
/// <remarks>
///     Mirrors the detection logic of the route-map generator (verb attributes, controller <c>[Route]</c>
///     prefixes, and <c>Map*</c> calls) but emits records for the index rather than a formatted map. Routes
///     built from constants or interpolation are not resolved. The route-to-handler edge is wired by the
///     semantic route analyzer in Phase 4; here the handler name is recorded in metadata for that pass.
/// </remarks>
public sealed class SyntaxRouteExtractor
{
    private static readonly Dictionary<string, string> VerbByAttribute = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET",
        ["HttpPost"] = "POST",
        ["HttpPut"] = "PUT",
        ["HttpDelete"] = "DELETE",
        ["HttpPatch"] = "PATCH",
    };

    private static readonly Dictionary<string, string> VerbByMapMethod = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH",
    };

    /// <summary>
    ///     Extracts routes declared in a C# file.
    /// </summary>
    /// <param name="normalizedPath">The forward-slash relative path used to key records to the file.</param>
    /// <param name="content">The file's source text.</param>
    /// <returns>The extracted routes; empty when none are found or the file fails to parse.</returns>
    public IReadOnlyList<RouteRecord> Extract(string normalizedPath, string content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        SyntaxNode root;
        try
        {
            root = CSharpSyntaxTree.ParseText(content).GetRoot();
        }
        catch
        {
            return [];
        }

        return Extract(normalizedPath, root);
    }

    /// <summary>
    ///     Extracts routes from an already-parsed C# syntax root (R47), so the semantic index pipeline parses each
    ///     file once and shares the tree with the symbol/chunk extractor instead of re-parsing here. Produces
    ///     byte-identical records to the content overload for the same source.
    /// </summary>
    /// <param name="normalizedPath">The forward-slash relative path used to key records to the file.</param>
    /// <param name="root">The parsed syntax root of the file.</param>
    /// <returns>The extracted routes; empty when none are found.</returns>
    public IReadOnlyList<RouteRecord> Extract(string normalizedPath, SyntaxNode root)
    {
        var routes = new List<RouteRecord>();
        CollectControllerRoutes(root, normalizedPath, routes);
        CollectMinimalApiRoutes(root, normalizedPath, routes);
        return routes;
    }

    private static void CollectControllerRoutes(SyntaxNode root, string path, List<RouteRecord> routes)
    {
        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var controllerRoute = string.Empty;
            foreach (var attribute in EnumerateAttributes(type.AttributeLists))
            {
                if (SimpleName(attribute) == "Route" && FirstStringArgument(attribute) is { } prefix)
                    controllerRoute = prefix.TrimEnd('/');
            }

            var controllerName = type.Identifier.ValueText;

            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                var handler = method.Identifier.ValueText;
                foreach (var attribute in EnumerateAttributes(method.AttributeLists))
                {
                    var name = SimpleName(attribute);
                    var routeArgument = FirstStringArgument(attribute);
                    string? verb = VerbByAttribute.TryGetValue(name, out var mapped) ? mapped
                        : name == "Route" && routeArgument is not null ? "GET"
                        : null;
                    if (verb is null)
                        continue;

                    var pattern = NormalizePattern(CombineRoutes(controllerRoute, routeArgument ?? handler));
                    routes.Add(BuildRoute(verb, pattern, path, method, "mvc", $"{controllerName}.{handler}"));
                }
            }
        }
    }

    private static void CollectMinimalApiRoutes(SyntaxNode root, string path, List<RouteRecord> routes)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
                continue;
            if (!VerbByMapMethod.TryGetValue(member.Name.Identifier.ValueText, out var verb))
                continue;
            if (invocation.ArgumentList.Arguments.Count == 0)
                continue;
            if (StringValue(invocation.ArgumentList.Arguments[0].Expression) is not { } pattern)
                continue;

            routes.Add(BuildRoute(verb, NormalizePattern(pattern), path, invocation, "minimal-api", handler: null));
        }
    }

    private static RouteRecord BuildRoute(string verb, string pattern, string path, SyntaxNode node, string sourceKind, string? handler)
    {
        var span = node.GetLocation().GetLineSpan();
        return new RouteRecord(
            RouteId: $"route:{verb}:{pattern}",
            HttpMethod: verb,
            RoutePattern: pattern,
            FilePath: path,
            StartLine: span.StartLinePosition.Line + 1,
            EndLine: span.EndLinePosition.Line + 1,
            SourceKind: sourceKind,
            HandlerSymbolId: null,
            MetadataJson: handler is null ? null : $"{{\"handler\":\"{EscapeJson(handler)}\"}}");
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
            if (StringValue(argument.Expression) is { } value)
                return value;
        return null;
    }

    private static string? StringValue(ExpressionSyntax expression)
        => expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    private static string CombineRoutes(string prefix, string actionRoute)
    {
        if (string.IsNullOrEmpty(prefix))
            return actionRoute;
        if (string.IsNullOrEmpty(actionRoute))
            return prefix;
        return $"{prefix.TrimEnd('/')}/{actionRoute.TrimStart('/')}";
    }

    private static string EscapeJson(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
