using Fuse.Plugins.Abstractions.Skeleton;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Plugins.Languages.CSharp.Roslyn;

/// <summary>
///     Roslyn-based <see cref="ISkeletonExtractor" />. Parses the C# syntax tree and removes member bodies,
///     keeping type and member signatures.
/// </summary>
/// <remarks>
///     Unlike the regex skeleton extractor, this works from a parsed tree, so conditional compilation, partial
///     classes, and braces inside strings do not desynchronize a depth counter. This is the fix for the
///     skeleton collapse the regex extractor exhibits on heavily conditional code.
/// </remarks>
public sealed class RoslynSkeletonExtractor : ISkeletonExtractor
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public string ExtractSkeleton(string content, bool publicApiOnly = false)
    {
        if (string.IsNullOrEmpty(content))
            return string.Empty;

        SyntaxNode root;
        try
        {
            root = CSharpSyntaxTree.ParseText(content).GetRoot();
        }
        catch
        {
            return content;
        }

        var rewriter = new BodyStrippingRewriter(publicApiOnly);
        var rewritten = rewriter.Visit(root);
        if (rewritten is null)
            return string.Empty;

        return rewritten.NormalizeWhitespace().ToFullString().Trim();
    }

    // Removes method, constructor, and operator bodies (replacing them with a semicolon), strips property and
    // indexer accessor bodies, and drops non-public members when publicApiOnly is set.
    private sealed class BodyStrippingRewriter(bool publicApiOnly) : CSharpSyntaxRewriter
    {
        private static readonly SyntaxToken Semicolon = SyntaxFactory.Token(SyntaxKind.SemicolonToken);

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (ShouldDrop(node, node.Modifiers))
                return null;

            return node
                .WithBody(null)
                .WithExpressionBody(null)
                .WithSemicolonToken(Semicolon);
        }

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            if (ShouldDrop(node, node.Modifiers))
                return null;

            return node
                .WithBody(null)
                .WithExpressionBody(null)
                .WithInitializer(null)
                .WithSemicolonToken(Semicolon);
        }

        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node) =>
            ShouldDrop(node, node.Modifiers)
                ? null
                : node.WithBody(null).WithExpressionBody(null).WithSemicolonToken(Semicolon);

        public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) =>
            ShouldDrop(node, node.Modifiers)
                ? null
                : node.WithBody(null).WithExpressionBody(null).WithSemicolonToken(Semicolon);

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            if (ShouldDrop(node, node.Modifiers))
                return null;

            // Collapse an expression-bodied property to an auto-getter; strip accessor bodies otherwise.
            if (node.ExpressionBody is not null)
            {
                return node
                    .WithExpressionBody(null)
                    .WithAccessorList(AutoGetter())
                    .WithSemicolonToken(default);
            }

            return node.WithAccessorList(StripAccessorBodies(node.AccessorList));
        }

        public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            if (ShouldDrop(node, node.Modifiers))
                return null;

            return node.WithAccessorList(StripAccessorBodies(node.AccessorList));
        }

        public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node) =>
            ShouldDrop(node, node.Modifiers) ? null : node;

        public override SyntaxNode? VisitEventFieldDeclaration(EventFieldDeclarationSyntax node) =>
            ShouldDrop(node, node.Modifiers) ? null : node;

        // Keep type declarations (recurse into members) but drop non-public types when publicApiOnly is set.
        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node) =>
            DropTypeOrRecurse(node, node.Modifiers, base.VisitClassDeclaration);

        public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node) =>
            DropTypeOrRecurse(node, node.Modifiers, base.VisitStructDeclaration);

        public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node) =>
            DropTypeOrRecurse(node, node.Modifiers, base.VisitRecordDeclaration);

        public override SyntaxNode? VisitInterfaceDeclaration(InterfaceDeclarationSyntax node) =>
            DropTypeOrRecurse(node, node.Modifiers, base.VisitInterfaceDeclaration);

        private SyntaxNode? DropTypeOrRecurse<T>(T node, SyntaxTokenList modifiers, Func<T, SyntaxNode?> recurse)
            where T : SyntaxNode
        {
            // A nested type is public API when it is public or protected; a top-level type with no accessibility
            // modifier is internal and dropped under publicApiOnly.
            if (publicApiOnly && !IsPublicOrProtected(modifiers))
                return null;

            return recurse(node);
        }

        // Interface members carry no accessibility modifiers but are public, so they are never dropped here.
        private bool ShouldDrop(SyntaxNode node, SyntaxTokenList modifiers)
        {
            if (!publicApiOnly)
                return false;

            if (node.Parent is InterfaceDeclarationSyntax)
                return false;

            return !IsPublicOrProtected(modifiers);
        }

        private static bool IsPublicOrProtected(SyntaxTokenList modifiers) =>
            modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword));

        private static AccessorListSyntax? StripAccessorBodies(AccessorListSyntax? accessors)
        {
            if (accessors is null)
                return null;

            var stripped = accessors.Accessors.Select(a =>
                a.WithBody(null).WithExpressionBody(null).WithSemicolonToken(Semicolon));
            return accessors.WithAccessors(SyntaxFactory.List(stripped));
        }

        private static AccessorListSyntax AutoGetter() =>
            SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                    .WithSemicolonToken(Semicolon)));
    }
}
