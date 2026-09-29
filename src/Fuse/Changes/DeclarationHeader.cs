using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Changes;

/// <summary>
///     A declaration's header as it is written, on one line. This is the text a cause quotes, so it keeps the file's own
///     spelling, where <see cref="DeclarationNode.Surface"/> flattens the tokens and pads the punctuation for comparison.
/// </summary>
internal static class DeclarationHeader
{
    /// <summary>
    ///     The header of <paramref name="node"/>, with its trivia dropped: it stops before a member's body, accessor list or
    ///     expression body and before a type's members, so the line never carries code. A field or event field variable,
    ///     which is what <see cref="FileDeclarations"/> keys a field by, is its declaration's attributes, modifiers and type
    ///     followed by its own name, without an initializer; a constant keeps its value, because the value is part of what
    ///     other files see.
    /// </summary>
    public static string Of(SyntaxNode node)
    {
        if (node is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Parent: BaseFieldDeclarationSyntax field } declaration } variable)
        {
            var header = $"{Text(field, declaration.Type.Span.End)} {variable.Identifier.Text}{variable.ArgumentList}";
            return OneLine(field.Modifiers.Any(SyntaxKind.ConstKeyword) && variable.Initializer is { } value ? $"{header} {value}" : header);
        }

        int? cut = node switch
        {
            BaseMethodDeclarationSyntax method => ((SyntaxNode?)method.Body ?? method.ExpressionBody)?.SpanStart ?? StartOf(method.SemicolonToken),
            AccessorDeclarationSyntax accessor => ((SyntaxNode?)accessor.Body ?? accessor.ExpressionBody)?.SpanStart ?? StartOf(accessor.SemicolonToken),
            PropertyDeclarationSyntax property => ((SyntaxNode?)property.AccessorList ?? property.ExpressionBody)?.SpanStart,
            IndexerDeclarationSyntax indexer => ((SyntaxNode?)indexer.AccessorList ?? indexer.ExpressionBody)?.SpanStart,
            EventDeclarationSyntax @event => @event.AccessorList?.SpanStart ?? StartOf(@event.SemicolonToken),
            BaseTypeDeclarationSyntax type => StartOf(type.OpenBraceToken) ?? (type is TypeDeclarationSyntax t ? StartOf(t.SemicolonToken) : null),
            DelegateDeclarationSyntax @delegate => StartOf(@delegate.SemicolonToken),
            _ => null,
        };
        return OneLine(Text(node, cut ?? node.Span.End));
    }

    /// <summary>
    ///     The source text from the start of <paramref name="node"/>, past its leading trivia, to <paramref name="end"/>. It
    ///     reads the span from the tree's text, so a type's members or a method's body are never copied to be cut off.
    /// </summary>
    private static string Text(SyntaxNode node, int end) =>
        node.SyntaxTree.GetText().ToString(TextSpan.FromBounds(node.SpanStart, Math.Clamp(end, node.SpanStart, node.Span.End)));

    /// <summary>Attributes, constraints and long parameter lists can span lines; the cause line is one line.</summary>
    private static string OneLine(string header) => string.Join(' ', header.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int? StartOf(SyntaxToken token) => token.IsKind(SyntaxKind.None) || token.IsMissing ? null : token.SpanStart;
}
