using System.Text;
using Fuse.Plugins.Abstractions.Maps;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Plugins.Languages.CSharp.Roslyn.Maps;

/// <summary>
///     Builds an HTTP route map from ASP.NET controller actions and minimal-API registrations in
///     <c>.cs</c> files using Roslyn syntax analysis.
/// </summary>
/// <remarks>
///     Attribute routes built from constants or interpolation are not resolved; verb-only actions fall back
///     to the handler name as the path. Rows are emitted in case-insensitive order inside a
///     <c>&lt;!-- fuse:route-map --&gt;</c> comment block.
/// </remarks>
public sealed class RoslynRouteMapGenerator : IRouteMapGenerator
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

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public string Generate(IReadOnlyDictionary<string, string> fileContents)
    {
        var rows = new List<string>();
        foreach (var (path, content) in fileContents)
        {
            if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var root = TryParse(content);
            if (root is null)
                continue;

            CollectControllerRoutes(root, path, rows);
            CollectMinimalApiRoutes(root, path, rows);
        }

        if (rows.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<!-- fuse:route-map");
        sb.AppendLine("VERB   PATH                                     HANDLER");
        foreach (var row in rows.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine(row);
        sb.AppendLine("-->");
        return sb.ToString();
    }

    private static void CollectControllerRoutes(SyntaxNode root, string path, List<string> rows)
    {
        foreach (var type in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            var controllerRoute = string.Empty;
            foreach (var attribute in EnumerateAttributes(type.AttributeLists))
            {
                if (SimpleName(attribute) == "Route" && FirstStringArgument(attribute) is { } prefix)
                    controllerRoute = prefix.TrimEnd('/');
            }

            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
            {
                var handler = method.Identifier.ValueText;
                foreach (var attribute in EnumerateAttributes(method.AttributeLists))
                {
                    var name = SimpleName(attribute);
                    var routeArgument = FirstStringArgument(attribute);

                    if (VerbByAttribute.TryGetValue(name, out var verb))
                    {
                        var route = CombineRoutes(controllerRoute, routeArgument ?? handler);
                        rows.Add(Format(verb, route, handler, path));
                    }
                    else if (name == "Route" && routeArgument is not null)
                    {
                        var route = CombineRoutes(controllerRoute, routeArgument);
                        rows.Add(Format("GET", route, handler, path));
                    }
                }
            }
        }
    }

    private static void CollectMinimalApiRoutes(SyntaxNode root, string path, List<string> rows)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member)
                continue;
            if (!VerbByMapMethod.TryGetValue(member.Name.Identifier.ValueText, out var verb))
                continue;
            if (invocation.ArgumentList.Arguments.Count == 0)
                continue;
            if (StringValue(invocation.ArgumentList.Arguments[0].Expression) is not { } route)
                continue;

            rows.Add(Format(verb, route, "minimal-api", path));
        }
    }

    private static string Format(string verb, string route, string handler, string path)
        => $"{verb,-6} {route,-40} {handler} ({path})";

    private static IEnumerable<AttributeSyntax> EnumerateAttributes(SyntaxList<AttributeListSyntax> lists)
    {
        foreach (var list in lists)
            foreach (var attribute in list.Attributes)
                yield return attribute;
    }

    private static string SimpleName(AttributeSyntax attribute)
        => attribute.Name.ToString().Split('.').Last();

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

    private static SyntaxNode? TryParse(string content)
    {
        try
        {
            return CSharpSyntaxTree.ParseText(content).GetRoot();
        }
        catch
        {
            return null;
        }
    }
}
