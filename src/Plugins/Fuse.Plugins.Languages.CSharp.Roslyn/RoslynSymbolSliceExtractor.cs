using Fuse.Plugins.Abstractions.Outline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Plugins.Languages.CSharp.Roslyn;

/// <summary>
///     Roslyn-based <see cref="ISymbolSliceExtractor" />. Keeps the named member in full and reduces every other
///     member of the file to its signature.
/// </summary>
public sealed class RoslynSymbolSliceExtractor : ISymbolSliceExtractor
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public string? ExtractSlice(string content, string memberName)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrWhiteSpace(memberName))
            return null;

        SyntaxNode root;
        try
        {
            root = CSharpSyntaxTree.ParseText(content).GetRoot();
        }
        catch
        {
            return null;
        }

        if (!DeclaresMember(root, memberName))
            return null;

        var rewriter = new SliceRewriter(memberName);
        var rewritten = rewriter.Visit(root);
        return rewritten?.NormalizeWhitespace().ToFullString().Trim();
    }

    private static bool DeclaresMember(SyntaxNode root, string memberName) =>
        root.DescendantNodes().Any(node => node switch
        {
            MethodDeclarationSyntax m => m.Identifier.ValueText == memberName,
            PropertyDeclarationSyntax p => p.Identifier.ValueText == memberName,
            _ => false,
        });

    // Strips the body of every member except those whose name matches the target, which are kept verbatim.
    private sealed class SliceRewriter(string target) : CSharpSyntaxRewriter
    {
        private static readonly SyntaxToken Semicolon = SyntaxFactory.Token(SyntaxKind.SemicolonToken);

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
            node.Identifier.ValueText == target
                ? node
                : node.WithBody(null).WithExpressionBody(null).WithSemicolonToken(Semicolon);

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (node.Identifier.ValueText == target)
                return node;

            if (node.ExpressionBody is not null)
            {
                return node
                    .WithExpressionBody(null)
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithSemicolonToken(Semicolon))))
                    .WithSemicolonToken(default);
            }

            return node.WithAccessorList(StripAccessorBodies(node.AccessorList));
        }

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            node.WithBody(null).WithExpressionBody(null).WithInitializer(null).WithSemicolonToken(Semicolon);

        private static AccessorListSyntax? StripAccessorBodies(AccessorListSyntax? accessors)
        {
            if (accessors is null)
                return null;

            var stripped = accessors.Accessors.Select(a =>
                a.WithBody(null).WithExpressionBody(null).WithSemicolonToken(Semicolon));
            return accessors.WithAccessors(SyntaxFactory.List(stripped));
        }
    }
}
