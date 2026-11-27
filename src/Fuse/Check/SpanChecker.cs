using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Fuse.Changes;
using Fuse.Check.Model;
using Fuse.Paths;
using Fuse.Workspace.Generations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Check;

/// <summary>
///     Fast span-level checker for <see cref="CheckOptions.InteractiveFast"/> mode. When a file has only method or
///     accessor body edits, binds only the edited member spans via <see cref="SemanticModel.GetDiagnostics(TextSpan?, CancellationToken)"/>
///     and skips whole-file binding. Falls back to full-file binding whenever declarations, usings, directives, or
///     code outside member bodies changed, when the check runs compilation-wide analyzers, which cannot run on spans,
///     or when per-file analyzers apply: the normal check reuses HEAD's stored analyzer results, while per-span runs
///     would run them on HEAD as well.
/// </summary>
internal sealed class SpanChecker
{
    private readonly RepoRoot _root;
    private readonly AnalyzerSelector _analyzers;

    /// <param name="root">The repository, for canonical relative error paths.</param>
    /// <param name="configurationGeneration">Changes whenever project configuration (project files, editorconfig) may have changed.</param>
    /// <param name="log">Where the analyzers' plans are written.</param>
    public SpanChecker(RepoRoot root, Func<int> configurationGeneration, Action<string> log)
    {
        _root = root;
        _analyzers = new AnalyzerSelector(configurationGeneration, log);
    }

    /// <summary>A pair of matching member nodes from HEAD and the working tree for span-level comparison.</summary>
    internal readonly record struct EditedSpan(SyntaxNode BeforeNode, SyntaxNode AfterNode);

    /// <summary>
    ///     Attempts to check <paramref name="path"/> by binding only edited member body spans across every target
    ///     framework compiling it. Returns null when any target framework has declaration, using, directive, syntax,
    ///     or non-body changes, or runs compilation-wide or per-file analyzers, and requires full-file binding.
    /// </summary>
    public async Task<IReadOnlyList<CompilerError>?> TryCheckFileAsync(
        RepoPath path,
        Generation generation,
        CheckContext context,
        CancellationToken cancellationToken)
    {
        if (!path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(path.Absolute))
            return null;

        var ids = generation.Current.GetDocumentIdsWithFilePath(path.Absolute);
        if (ids.IsEmpty)
            return null;

        var collected = new List<CompilerError>();
        foreach (var id in ids)
        {
            var currentDoc = generation.Current.GetDocument(id);
            var baselineDoc = generation.Baseline.GetDocument(id);
            if (currentDoc is null || baselineDoc is null)
                return null;

            var beforeRoot = await baselineDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            var afterRoot = await currentDoc.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (beforeRoot is null || afterRoot is null)
                return null;

            var spans = TryGetBodyOnlySpans(beforeRoot, afterRoot);
            if (spans is null)
                return null;

            context.CountDocumentBound();
            if (spans.Count == 0)
                continue;

            var updateStart = Stopwatch.GetTimestamp();
            var model = await currentDoc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            context.AddUpdateTicks(Stopwatch.GetTimestamp() - updateStart);
            if (model is null)
                return null;

            if (await PerFileAnalyzersAsync(currentDoc, model, path, context, cancellationToken).ConfigureAwait(false) is not { } analyzers)
                return null;
            if (analyzers.WithAnalyzers is not null && !analyzers.PerFile.IsEmpty)
                return null;

            SemanticModel? baselineModel = null;
            foreach (var pair in spans)
            {
                var bindStart = Stopwatch.GetTimestamp();
                var afterDiagnostics = model.GetDiagnostics(pair.AfterNode.Span, cancellationToken);
                context.AddCompilerTicks(Stopwatch.GetTimestamp() - bindStart);

                var afterErrors = ToCompilerErrors(afterDiagnostics);
                if (afterErrors.Count == 0)
                    continue;

                baselineModel ??= await baselineDoc.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                var baselineErrors = baselineModel is null
                    ? new List<CompilerError>()
                    : ToCompilerErrors(baselineModel.GetDiagnostics(pair.BeforeNode.Span, cancellationToken));

                collected.AddRange(ErrorDelta.Introduced(afterErrors, baselineErrors));
            }
        }

        return collected
            .Distinct()
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ThenBy(e => e.Line)
            .ThenBy(e => e.Column)
            .ToList();
    }

    /// <summary>
    ///     Identifies the edited member body node pairs between <paramref name="beforeRoot"/> and
    ///     <paramref name="afterRoot"/>, or returns null when full-file binding is required.
    /// </summary>
    public static IReadOnlyList<EditedSpan>? TryGetBodyOnlySpans(SyntaxNode? beforeRoot, SyntaxNode afterRoot)
    {
        ArgumentNullException.ThrowIfNull(afterRoot);
        if (beforeRoot is null)
            return null;

        if (HasSyntaxErrors(beforeRoot) || HasSyntaxErrors(afterRoot))
            return null;

        if ((beforeRoot.ContainsDirectives || afterRoot.ContainsDirectives) && !DirectivesMatch(beforeRoot, afterRoot))
            return null;

        var beforeDecls = FileDeclarations.Of(beforeRoot);
        var afterDecls = FileDeclarations.Of(afterRoot);
        if (SurfaceDiff.Compare(beforeDecls, afterDecls).Changes.Count > 0)
            return null;

        var codeDiff = CodeDiff.Find(beforeRoot, afterRoot);
        if (codeDiff.Count == 0)
            return TrySpansForTriviaOnlyEdit(beforeRoot, afterRoot);

        var result = new List<EditedSpan>(codeDiff.Count);
        foreach (var changed in codeDiff)
        {
            if (!IsSupportedBodyNode(changed))
                return null;

            var match = FindMatchingBeforeNode(beforeDecls, afterDecls, changed);
            if (match is null || match.RawKind != changed.RawKind)
                return null;

            if (!IsBodyOnlyMemberChange(match, changed))
                return null;

            if (changed is PropertyDeclarationSyntax afterProp && match is PropertyDeclarationSyntax beforeProp
                && afterProp.AccessorList is { } afterAccessors && beforeProp.AccessorList is { } beforeAccessors
                && afterAccessors.Accessors.Count == beforeAccessors.Accessors.Count)
            {
                var addedAnyAccessor = false;
                for (var i = 0; i < afterAccessors.Accessors.Count; i++)
                {
                    if (!SyntaxFactory.AreEquivalent(beforeAccessors.Accessors[i], afterAccessors.Accessors[i], topLevel: false))
                    {
                        result.Add(new EditedSpan(beforeAccessors.Accessors[i], afterAccessors.Accessors[i]));
                        addedAnyAccessor = true;
                    }
                }

                if (addedAnyAccessor)
                    continue;
            }

            result.Add(new EditedSpan(match, changed));
        }

        if (HasChangedDocCommentOutsideSpans(beforeRoot, afterRoot, result))
            return null;

        return result;
    }

    private static IReadOnlyList<EditedSpan>? TrySpansForTriviaOnlyEdit(SyntaxNode beforeRoot, SyntaxNode afterRoot)
    {
        var beforeText = beforeRoot.SyntaxTree.GetText();
        var afterText = afterRoot.SyntaxTree.GetText();
        if (beforeText.ContentEquals(afterText))
            return [];

        if (ChangedTextSpan(beforeText, afterText) is not { } changedSpan)
            return [];

        var probePos = Math.Clamp(changedSpan.Start, afterRoot.FullSpan.Start, Math.Max(afterRoot.FullSpan.Start, afterRoot.FullSpan.End - 1));
        var triviaOrToken = afterRoot.FindTrivia(probePos);
        var parent = triviaOrToken.Token.Parent ?? afterRoot.FindToken(probePos).Parent;
        var member = parent?.AncestorsAndSelf().FirstOrDefault(IsInsideEditableBody);
        if (member is null || !IsInsideBodySpan(member, changedSpan))
            return null;

        var beforeMember = MatchMemberByPosition(beforeRoot, afterRoot, member);
        return beforeMember is null ? null : [new EditedSpan(beforeMember, member)];
    }

    private static TextSpan? ChangedTextSpan(SourceText beforeText, SourceText afterText)
    {
        var beforeLines = beforeText.Lines;
        var afterLines = afterText.Lines;
        var minLines = Math.Min(beforeLines.Count, afterLines.Count);

        var prefix = 0;
        while (prefix < minLines && string.Equals(beforeLines[prefix].ToString(), afterLines[prefix].ToString(), StringComparison.Ordinal))
            prefix++;

        if (prefix == beforeLines.Count && prefix == afterLines.Count)
            return null;

        var beforeEnd = beforeLines.Count - 1;
        var afterEnd = afterLines.Count - 1;
        while (beforeEnd >= prefix && afterEnd >= prefix
            && string.Equals(beforeLines[beforeEnd].ToString(), afterLines[afterEnd].ToString(), StringComparison.Ordinal))
        {
            beforeEnd--;
            afterEnd--;
        }

        var startLine = Math.Min(prefix, Math.Max(0, afterLines.Count - 1));
        var endLine = Math.Clamp(afterEnd, startLine, Math.Max(0, afterLines.Count - 1));
        return TextSpan.FromBounds(afterLines[startLine].Start, afterLines[endLine].End);
    }

    private static bool IsInsideEditableBody(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax;

    private static bool IsInsideBodySpan(SyntaxNode member, TextSpan changeSpan)
    {
        SyntaxNode? body = member switch
        {
            BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
            PropertyDeclarationSyntax p => (SyntaxNode?)p.AccessorList ?? p.ExpressionBody,
            IndexerDeclarationSyntax i => (SyntaxNode?)i.AccessorList ?? i.ExpressionBody,
            EventDeclarationSyntax e => e.AccessorList,
            _ => null,
        };
        return body is not null && body.FullSpan.Contains(changeSpan);
    }

    private static SyntaxNode? MatchMemberByPosition(SyntaxNode beforeRoot, SyntaxNode afterRoot, SyntaxNode afterMember)
    {
        var afterDecls = FileDeclarations.Of(afterRoot);
        var beforeDecls = FileDeclarations.Of(beforeRoot);
        return FindMatchingBeforeNode(beforeDecls, afterDecls, afterMember);
    }

    private static bool HasSyntaxErrors(SyntaxNode root) =>
        root.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);

    private static bool DirectivesMatch(SyntaxNode beforeRoot, SyntaxNode afterRoot)
    {
        var beforeDirs = CollectDirectives(beforeRoot);
        var afterDirs = CollectDirectives(afterRoot);
        if (beforeDirs.Count != afterDirs.Count)
            return false;
        for (var i = 0; i < beforeDirs.Count; i++)
        {
            if (!SyntaxFactory.AreEquivalent(beforeDirs[i], afterDirs[i]))
                return false;
        }

        return true;
    }

    private static List<DirectiveTriviaSyntax> CollectDirectives(SyntaxNode root)
    {
        var list = new List<DirectiveTriviaSyntax>();
        for (var dir = root.GetFirstDirective(); dir is not null; dir = dir.GetNextDirective())
            list.Add(dir);
        return list;
    }

    private static bool IsSupportedBodyNode(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax;

    private static SyntaxNode? FindMatchingBeforeNode(FileDeclarations beforeDecls, FileDeclarations afterDecls, SyntaxNode afterNode)
    {
        foreach (var decl in afterDecls.All)
        {
            for (var i = 0; i < decl.Parts.Count; i++)
            {
                if (!ReferenceEquals(decl.Parts[i], afterNode))
                    continue;
                var beforeDecl = beforeDecls.Find(decl.Key);
                if (beforeDecl is null || i >= beforeDecl.Parts.Count)
                    return null;
                return beforeDecl.Parts[i];
            }
        }

        return null;
    }

    private static bool IsBodyOnlyMemberChange(SyntaxNode before, SyntaxNode after) =>
        (before, after) switch
        {
            (BaseMethodDeclarationSyntax b, BaseMethodDeclarationSyntax a) =>
                (b.Body is not null || b.ExpressionBody is not null)
                && (a.Body is not null || a.ExpressionBody is not null),
            (PropertyDeclarationSyntax b, PropertyDeclarationSyntax a) =>
                (b.AccessorList is not null || b.ExpressionBody is not null)
                && (a.AccessorList is not null || a.ExpressionBody is not null)
                && AreEquivalentOrBothNull(b.Initializer, a.Initializer),
            (IndexerDeclarationSyntax b, IndexerDeclarationSyntax a) =>
                (b.AccessorList is not null || b.ExpressionBody is not null)
                && (a.AccessorList is not null || a.ExpressionBody is not null),
            (EventDeclarationSyntax b, EventDeclarationSyntax a) =>
                b.AccessorList is not null && a.AccessorList is not null,
            _ => false,
        };

    private static bool AreEquivalentOrBothNull(SyntaxNode? first, SyntaxNode? second)
    {
        if (first is null && second is null)
            return true;
        if (first is null || second is null)
            return false;
        return SyntaxFactory.AreEquivalent(first, second, topLevel: false);
    }

    private static bool HasChangedDocCommentOutsideSpans(SyntaxNode beforeRoot, SyntaxNode afterRoot, List<EditedSpan> spans)
    {
        if (!beforeRoot.HasStructuredTrivia && !afterRoot.HasStructuredTrivia)
            return false;

        var beforeDocs = CollectDocCommentsOutside(beforeRoot, spans, before: true);
        var afterDocs = CollectDocCommentsOutside(afterRoot, spans, before: false);
        return !beforeDocs.SequenceEqual(afterDocs, StringComparer.Ordinal);
    }

    private static List<string> CollectDocCommentsOutside(SyntaxNode root, List<EditedSpan> spans, bool before)
    {
        var list = new List<string>();
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: false))
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                continue;
            var inside = spans.Any(s => (before ? s.BeforeNode : s.AfterNode).FullSpan.Contains(trivia.Span));
            if (!inside)
                list.Add(trivia.ToFullString());
        }

        return list;
    }

    private List<CompilerError> ToCompilerErrors(IEnumerable<Diagnostic> diagnostics)
    {
        var list = new List<CompilerError>();
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error || diagnostic.IsSuppressed)
                continue;
            if (!diagnostic.Location.IsInSource && diagnostic.Location.Kind != LocationKind.ExternalFile)
                continue;
            var lineSpan = diagnostic.Location.GetMappedLineSpan();
            if (!lineSpan.IsValid || string.IsNullOrEmpty(lineSpan.Path))
                continue;
            list.Add(new CompilerError(
                _root.PathOf(lineSpan.Path).Relative,
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1,
                diagnostic.Id,
                diagnostic.GetMessage(CultureInfo.InvariantCulture),
                FromAnalyzer: false));
        }

        return list;
    }

    /// <summary>
    ///     The per-file analyzers the normal check would run for <paramref name="document"/>, or null when the span
    ///     check cannot answer it: the check runs compilation-wide analyzers, which cannot run on spans. When the
    ///     check leaves them out they stay out, named as in the normal check.
    /// </summary>
    private async Task<SpanAnalyzers?> PerFileAnalyzersAsync(Document document, SemanticModel model, RepoPath path, CheckContext context, CancellationToken cancellationToken)
    {
        var withAnalyzers = _analyzers.For(document.Project, model.Compilation);
        if (withAnalyzers is null)
            return new SpanAnalyzers(null, []);

        AnalyzerPlan plan;
        try
        {
            using var planning = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Run.Deadline);
            plan = await _analyzers.PlanAsync(document.Project, withAnalyzers, model, planning.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The plan was not made before the check's deadline: the file keeps its compiler errors and counts as unfinished.
            context.MarkUnfinished(path.Relative);
            return new SpanAnalyzers(null, []);
        }

        if (!plan.CompilationWide.IsEmpty)
        {
            if (context.Run.CompilationWide)
                return null;
            foreach (var (analyzer, _) in plan.CompilationWide)
                context.MarkLeftOut(AnalyzerSelector.Name(analyzer));
        }

        return new SpanAnalyzers(withAnalyzers, plan.PerFile);
    }

    /// <summary>The per-file analyzers the normal check would run for one document of a span check.</summary>
    /// <param name="WithAnalyzers">The document's compilation wrapped with its error-capable analyzers, or null when none can report an error.</param>
    /// <param name="PerFile">The analyzers the normal check runs on the file: when any apply, the span check falls back to it.</param>
    private sealed record SpanAnalyzers(CompilationWithAnalyzers? WithAnalyzers, ImmutableArray<DiagnosticAnalyzer> PerFile);
}
