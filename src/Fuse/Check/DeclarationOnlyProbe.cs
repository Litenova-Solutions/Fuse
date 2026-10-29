using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Check;

/// <summary>
///     Decides whether a compilation-wide analyzer reads only declarations, by running it alone on the compilation and on
///     the same compilation with every body emptied, and comparing what it reports. Anything but two equal lists of
///     diagnostics keeps the analyzer body-reading, so a doubt reruns it.
/// </summary>
internal static class DeclarationOnlyProbe
{
    /// <summary>
    ///     True when <paramref name="analyzer"/> reports the same diagnostics with and without bodies. Any difference,
    ///     failure or cancellation answers false, as does an analyzer with no diagnostics of its own.
    /// </summary>
    public static async Task<bool> ClassifyAsync(Compilation compilation, DiagnosticAnalyzer analyzer, AnalyzerOptions options, CancellationToken cancellationToken)
    {
        try
        {
            if (analyzer.SupportedDiagnostics.IsEmpty)
                return false;
            if (EmptyBodies(compilation, cancellationToken) is not { } emptied)
                return false;
            var ids = analyzer.SupportedDiagnostics.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
            var analyzerOptions = new CompilationWithAnalyzersOptions(options, onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false);
            var withBodies = await compilation.WithAnalyzers([analyzer], analyzerOptions).GetAnalysisResultAsync([analyzer], cancellationToken).ConfigureAwait(false);
            var withoutBodies = await emptied.WithAnalyzers([analyzer], analyzerOptions).GetAnalysisResultAsync([analyzer], cancellationToken).ConfigureAwait(false);
            return Report(withBodies, ids).SequenceEqual(Report(withoutBodies, ids));
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            // Any doubt keeps today's behavior: the analyzer runs again after every edit.
            return false;
        }
    }

    /// <summary>The analyzer's diagnostics as comparable lines, sorted, so two runs compare as lists.</summary>
    private static List<(string Path, int Start, int Length, string Id, string Message)> Report(AnalysisResult result, HashSet<string> ids)
    {
        var lines = new List<(string Path, int Start, int Length, string Id, string Message)>();
        foreach (var diagnostic in result.GetAllDiagnostics())
        {
            if (!ids.Contains(diagnostic.Id))
                continue;
            var tree = diagnostic.Location.SourceTree;
            lines.Add((
                tree?.FilePath ?? "",
                tree is null ? 0 : diagnostic.Location.SourceSpan.Start,
                tree is null ? 0 : diagnostic.Location.SourceSpan.Length,
                diagnostic.Id,
                diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)));
        }

        lines.Sort();
        return lines;
    }

    /// <summary>
    ///     The compilation with every body emptied: each method, constructor, operator, accessor and local function holds
    ///     an empty block, each top-level statement but a local function is an empty statement, and declarations,
    ///     attributes, signatures and initializers stay as they are. Null when a tree is not C#.
    /// </summary>
    private static Compilation? EmptyBodies(Compilation compilation, CancellationToken cancellationToken)
    {
        var emptied = new List<SyntaxTree>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree.GetRoot(cancellationToken) is not CSharpSyntaxNode root)
                return null;
            emptied.Add(tree.WithRootAndOptions(new BodyEraser().Visit(root)!, tree.Options));
        }

        return compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(emptied);
    }

    /// <summary>Rewrites every body to an empty block, keeping the declarations around them.</summary>
    private sealed class BodyEraser : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
            WithEmptyBody(base.VisitMethodDeclaration(node) as MethodDeclarationSyntax ?? node);

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            WithEmptyBody(base.VisitConstructorDeclaration(node) as ConstructorDeclarationSyntax ?? node);

        public override SyntaxNode? VisitDestructorDeclaration(DestructorDeclarationSyntax node) =>
            WithEmptyBody(base.VisitDestructorDeclaration(node) as DestructorDeclarationSyntax ?? node);

        public override SyntaxNode? VisitOperatorDeclaration(OperatorDeclarationSyntax node) =>
            WithEmptyBody(base.VisitOperatorDeclaration(node) as OperatorDeclarationSyntax ?? node);

        public override SyntaxNode? VisitConversionOperatorDeclaration(ConversionOperatorDeclarationSyntax node) =>
            WithEmptyBody(base.VisitConversionOperatorDeclaration(node) as ConversionOperatorDeclarationSyntax ?? node);

        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
        {
            var visited = base.VisitAccessorDeclaration(node) as AccessorDeclarationSyntax ?? node;
            if (visited.Body is not null)
                return visited.WithBody(SyntaxFactory.Block());
            return visited.ExpressionBody is null ? visited : visited.WithBody(SyntaxFactory.Block()).WithExpressionBody(null).WithSemicolonToken(default);
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) =>
            WithEmptyBody(base.VisitLocalFunctionStatement(node) as LocalFunctionStatementSyntax ?? node);

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var visited = base.VisitPropertyDeclaration(node) as PropertyDeclarationSyntax ?? node;
            if (visited.AccessorList is not null || visited.ExpressionBody is null)
                return visited;
            return visited.WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(SyntaxFactory.Block()))))
                .WithExpressionBody(null)
                .WithSemicolonToken(default);
        }

        public override SyntaxNode? VisitIndexerDeclaration(IndexerDeclarationSyntax node)
        {
            var visited = base.VisitIndexerDeclaration(node) as IndexerDeclarationSyntax ?? node;
            if (visited.AccessorList is not null || visited.ExpressionBody is null)
                return visited;
            return visited.WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                    SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(SyntaxFactory.Block()))))
                .WithExpressionBody(null)
                .WithSemicolonToken(default);
        }

        public override SyntaxNode? VisitGlobalStatement(GlobalStatementSyntax node)
        {
            if (node.Statement is not LocalFunctionStatementSyntax)
                return node.WithStatement(SyntaxFactory.EmptyStatement());
            return base.VisitGlobalStatement(node);
        }

        private static MethodDeclarationSyntax WithEmptyBody(MethodDeclarationSyntax node)
        {
            if (node.Body is not null)
                return node.WithBody(SyntaxFactory.Block());
            return node.ExpressionBody is null ? node : node.WithBody(SyntaxFactory.Block()).WithExpressionBody(null).WithSemicolonToken(default);
        }

        private static ConstructorDeclarationSyntax WithEmptyBody(ConstructorDeclarationSyntax node)
        {
            if (node.Body is not null)
                return node.WithBody(SyntaxFactory.Block());
            return node.ExpressionBody is null ? node : node.WithBody(SyntaxFactory.Block()).WithExpressionBody(null).WithSemicolonToken(default);
        }

        private static DestructorDeclarationSyntax WithEmptyBody(DestructorDeclarationSyntax node)
        {
            if (node.Body is not null)
                return node.WithBody(SyntaxFactory.Block());
            return node.ExpressionBody is null ? node : node.WithBody(SyntaxFactory.Block()).WithExpressionBody(null).WithSemicolonToken(default);
        }

        private static OperatorDeclarationSyntax WithEmptyBody(OperatorDeclarationSyntax node)
        {
            if (node.Body is not null)
                return node.WithBody(SyntaxFactory.Block());
            return node.ExpressionBody is null ? node : node.WithBody(SyntaxFactory.Block()).WithExpressionBody(null).WithSemicolonToken(default);
        }

        private static ConversionOperatorDeclarationSyntax WithEmptyBody(ConversionOperatorDeclarationSyntax node)
        {
            if (node.Body is not null)
                return node.WithBody(SyntaxFactory.Block());
            return node.ExpressionBody is null ? node : node.WithBody(SyntaxFactory.Block()).WithExpressionBody(null).WithSemicolonToken(default);
        }

        private static LocalFunctionStatementSyntax WithEmptyBody(LocalFunctionStatementSyntax node)
        {
            if (node.Body is not null)
                return node.WithBody(SyntaxFactory.Block());
            return node.ExpressionBody is null ? node : node.WithBody(SyntaxFactory.Block()).WithExpressionBody(null).WithSemicolonToken(default);
        }
    }
}
