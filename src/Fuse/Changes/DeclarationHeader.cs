using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Changes;

/// <summary>
///     A declaration's header as it is written, on one line. This is the text a cause quotes, so it keeps the file's own
///     spelling, where <see cref="DeclarationNode.Surface"/> flattens the tokens and pads the punctuation for comparison.
/// </summary>
internal static class DeclarationHeader
{
    /// <summary>
    ///     The header of <paramref name="node"/>, with its trivia dropped: it stops before a member's body, accessor list or
    ///     expression body, before a type's members, and before a field's initializer, so the line never carries code.
    /// </summary>
    public static string Of(SyntaxNode node)
    {
        var text = node.WithoutLeadingTrivia().WithoutTrailingTrivia().ToFullString();
        int? cut = node switch
        {
            BaseMethodDeclarationSyntax method => ((SyntaxNode?)method.Body ?? method.ExpressionBody)?.SpanStart ?? StartOf(method.SemicolonToken),
            AccessorDeclarationSyntax accessor => ((SyntaxNode?)accessor.Body ?? accessor.ExpressionBody)?.SpanStart ?? StartOf(accessor.SemicolonToken),
            PropertyDeclarationSyntax property => ((SyntaxNode?)property.AccessorList ?? property.ExpressionBody)?.SpanStart,
            IndexerDeclarationSyntax indexer => ((SyntaxNode?)indexer.AccessorList ?? indexer.ExpressionBody)?.SpanStart,
            EventDeclarationSyntax @event => @event.AccessorList?.SpanStart ?? StartOf(@event.SemicolonToken),
            BaseFieldDeclarationSyntax field => field.Declaration.Variables.FirstOrDefault(v => v.Initializer is not null)?.Initializer!.SpanStart ?? StartOf(field.SemicolonToken),
            BaseTypeDeclarationSyntax type => StartOf(type.OpenBraceToken) ?? (type is TypeDeclarationSyntax t ? StartOf(t.SemicolonToken) : null),
            DelegateDeclarationSyntax @delegate => StartOf(@delegate.SemicolonToken),
            _ => null,
        };
        var header = cut is { } at ? text[..Math.Clamp(at - node.SpanStart, 0, text.Length)] : text;
        // Attributes, constraints and long parameter lists can span lines; the cause line is one line.
        return string.Join(' ', header.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static int? StartOf(SyntaxToken token) => token.IsKind(SyntaxKind.None) || token.IsMissing ? null : token.SpanStart;
}
