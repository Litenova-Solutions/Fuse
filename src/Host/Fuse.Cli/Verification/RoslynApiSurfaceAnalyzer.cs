#if FUSE_ROSLYN
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Cli.Verification;

/// <summary>
///     Roslyn syntax-only implementation of <see cref="IApiSurfaceAnalyzer" />. Parses each file's syntax
///     tree (no semantic model, no metadata references) to collect public and protected types and methods,
///     attribute route templates, and minimal-API route templates.
/// </summary>
/// <remarks>
///     Compiled only for the framework-dependent tool. The Native AOT build excludes the Roslyn package and
///     falls back to <see cref="RegexApiSurfaceAnalyzer" />, because Roslyn is not trim or AOT compatible.
///     This mirrors the independent Roslyn oracle used by the benchmark fidelity tool.
/// </remarks>
public sealed class RoslynApiSurfaceAnalyzer : IApiSurfaceAnalyzer
{
    /// <inheritdoc />
    public void Collect(string source, ISet<string> types, ISet<string> methods, ISet<string> routes)
    {
        if (string.IsNullOrWhiteSpace(source))
            return;

        SyntaxNode root;
        try
        {
            root = CSharpSyntaxTree.ParseText(source).GetRoot();
        }
        catch
        {
            return;
        }

        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (HasPublicOrProtected(type.Modifiers))
                types.Add(type.Identifier.ValueText);
        }

        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var parentType = method.Parent as BaseTypeDeclarationSyntax;
            var inInterface = parentType is InterfaceDeclarationSyntax;
            if (!inInterface && !HasPublicOrProtected(method.Modifiers))
                continue;

            if (parentType is not null && !inInterface && !HasPublicOrProtected(parentType.Modifiers))
                continue;

            methods.Add(method.Identifier.ValueText);
        }

        foreach (var attr in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            var simple = attr.Name.ToString().Split('.').Last();
            var isRoute = simple is "Route" or "HttpGet" or "HttpPost" or "HttpPut"
                or "HttpDelete" or "HttpPatch" or "HttpHead" or "HttpOptions";
            if (!isRoute || attr.ArgumentList is null)
                continue;

            foreach (var arg in attr.ArgumentList.Arguments)
                AddRouteLiteral(arg.Expression, routes);
        }

        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma)
                continue;

            var name = ma.Name.Identifier.ValueText;
            var isMap = name is "MapGet" or "MapPost" or "MapPut" or "MapDelete" or "MapPatch";
            if (!isMap || inv.ArgumentList.Arguments.Count == 0)
                continue;

            AddRouteLiteral(inv.ArgumentList.Arguments[0].Expression, routes);
        }
    }

    private static void AddRouteLiteral(ExpressionSyntax expression, ISet<string> routes)
    {
        if (expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression))
        {
            var value = lit.Token.ValueText;
            if (!string.IsNullOrWhiteSpace(value))
                routes.Add(value);
        }
    }

    private static bool HasPublicOrProtected(SyntaxTokenList modifiers) =>
        modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword));
}
#endif
