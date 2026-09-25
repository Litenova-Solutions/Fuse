using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Evals;

/// <summary>
///     Generates behavior mutations inside method bodies (the edits that keep compiling but change results):
///     flipping a comparison, shifting an integer constant by one, dropping a statement, or negating a boolean return.
/// </summary>
internal static class BehaviorMutator
{
    public static readonly string[] Kinds = ["flip-comparison", "off-by-one", "drop-statement", "negate-return"];

    public static FileEdit? Mutate(string path, string relative, string kind, Random random)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path), CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)).GetCompilationUnitRoot();
        var bodies = root.DescendantNodes().Where(n => n is BlockSyntax { Parent: BaseMethodDeclarationSyntax or AccessorDeclarationSyntax } or ArrowExpressionClauseSyntax).ToList();
        if (bodies.Count == 0)
            return null;
        T? Pick<T>(Func<T, bool>? filter = null)
            where T : SyntaxNode
        {
            var candidates = bodies.SelectMany(b => b.DescendantNodes().OfType<T>()).Where(filter ?? (_ => true)).ToList();
            return candidates.Count == 0 ? null : candidates[random.Next(candidates.Count)];
        }

        string Where(SyntaxNode node) => $"line {node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";

        switch (kind)
        {
            case "flip-comparison":
                {
                    var e = Pick<BinaryExpressionSyntax>(b => Flip(b.Kind()) is not null);
                    if (e is null)
                        return null;
                    var (newKind, token) = Flip(e.Kind())!.Value;
                    var flipped = SyntaxFactory.BinaryExpression(newKind, e.Left, SyntaxFactory.Token(token).WithTriviaFrom(e.OperatorToken), e.Right);
                    return new FileEdit(relative, kind, $"flipped {e.OperatorToken.Text} at {Where(e)}", root.ReplaceNode(e, flipped).ToFullString());
                }

            case "off-by-one":
                {
                    var literal = Pick<LiteralExpressionSyntax>(l => l.Token.Value is int && l.Parent is not CaseSwitchLabelSyntax and not AttributeArgumentSyntax);
                    if (literal is null)
                        return null;
                    var value = (int)literal.Token.Value! + 1;
                    return new FileEdit(relative, kind, $"changed {literal.Token.Text} to {value} at {Where(literal)}",
                        root.ReplaceNode(literal, SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(value)).WithTriviaFrom(literal)).ToFullString());
                }

            case "drop-statement":
                {
                    var statement = Pick<ExpressionStatementSyntax>(s => s.Parent is BlockSyntax);
                    if (statement is null)
                        return null;
                    return new FileEdit(relative, kind, $"dropped `{Short(statement)}` at {Where(statement)}", root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString());
                }

            case "negate-return":
                {
                    var ret = Pick<ReturnStatementSyntax>(r => r.Expression is LiteralExpressionSyntax l && (l.IsKind(SyntaxKind.TrueLiteralExpression) || l.IsKind(SyntaxKind.FalseLiteralExpression))
                                                              || r.Expression is BinaryExpressionSyntax b && Flip(b.Kind()) is not null);
                    if (ret?.Expression is null)
                        return null;
                    var negated = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, SyntaxFactory.ParenthesizedExpression(ret.Expression.WithoutTrivia())).WithTriviaFrom(ret.Expression);
                    return new FileEdit(relative, kind, $"negated `{Short(ret)}` at {Where(ret)}", root.ReplaceNode(ret.Expression, negated).ToFullString());
                }
        }

        return null;
    }

    private static string Short(SyntaxNode node)
    {
        var text = node.ToString().ReplaceLineEndings(" ");
        return text.Length > 60 ? text[..60] + "..." : text;
    }

    private static (SyntaxKind Kind, SyntaxKind Token)? Flip(SyntaxKind kind) => kind switch
    {
        SyntaxKind.LessThanExpression => (SyntaxKind.GreaterThanOrEqualExpression, SyntaxKind.GreaterThanEqualsToken),
        SyntaxKind.GreaterThanOrEqualExpression => (SyntaxKind.LessThanExpression, SyntaxKind.LessThanToken),
        SyntaxKind.GreaterThanExpression => (SyntaxKind.LessThanOrEqualExpression, SyntaxKind.LessThanEqualsToken),
        SyntaxKind.LessThanOrEqualExpression => (SyntaxKind.GreaterThanExpression, SyntaxKind.GreaterThanToken),
        SyntaxKind.EqualsExpression => (SyntaxKind.NotEqualsExpression, SyntaxKind.ExclamationEqualsToken),
        SyntaxKind.NotEqualsExpression => (SyntaxKind.EqualsExpression, SyntaxKind.EqualsEqualsToken),
        _ => null,
    };
}
