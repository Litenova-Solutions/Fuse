using System.Collections.Concurrent;
using Fuse.Repo;
using Microsoft.CodeAnalysis;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;
using FuseDiagnostic = Fuse.Protocol.Diagnostic;

namespace Fuse.Check;

/// <summary>
///     Computes the errors of one file (in every target framework it compiles for) or of whole projects, including
///     errors from analyzers that can report errors. Baseline results are cached per baseline generation, because
///     the baseline does not change between edits.
/// </summary>
internal sealed class DiagnosticCollector
{
    private readonly RepoRoot _root;
    private readonly AnalyzerSelector _analyzers = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<FuseDiagnostic>> _baselineCache = new(ChangeTracker.PathComparer);
    private int _cachedGeneration = -1;

    public DiagnosticCollector(RepoRoot root) => _root = root;

    /// <summary>Errors reported in <paramref name="path"/>, from the regular documents and from Razor-generated code mapped back to it.</summary>
    public async Task<IReadOnlyList<FuseDiagnostic>> ForFileAsync(Solution solution, string path, CancellationToken cancellationToken)
    {
        var result = new List<FuseDiagnostic>();
        foreach (var id in solution.GetDocumentIdsWithFilePath(path))
        {
            var document = solution.GetDocument(id);
            if (document is not null)
            {
                result.AddRange(await ForDocumentAsync(document, cancellationToken).ConfigureAwait(false));
                continue;
            }

            if (solution.GetAdditionalDocument(id) is { } additional)
                result.AddRange(await ForGeneratedFromAsync(solution.GetProject(additional.Project.Id)!, path, cancellationToken).ConfigureAwait(false));
        }

        return Distinct(result);
    }

    /// <summary>Same as <see cref="ForFileAsync"/> against the baseline, cached until the baseline changes.</summary>
    public async Task<IReadOnlyList<FuseDiagnostic>> ForBaselineFileAsync(Solution baseline, int generation, string path, CancellationToken cancellationToken)
    {
        if (generation != _cachedGeneration)
        {
            _baselineCache.Clear();
            _cachedGeneration = generation;
        }

        if (_baselineCache.TryGetValue(path, out var cached))
            return cached;
        var computed = await ForFileAsync(baseline, path, cancellationToken).ConfigureAwait(false);
        _baselineCache[path] = computed;
        return computed;
    }

    /// <summary>Same as <see cref="ForProjectAsync"/> against the baseline, cached until the baseline changes.</summary>
    public async Task<IReadOnlyList<FuseDiagnostic>> ForBaselineProjectAsync(Project project, int generation, CancellationToken cancellationToken)
    {
        if (generation != _cachedGeneration)
        {
            _baselineCache.Clear();
            _cachedGeneration = generation;
        }

        var key = "project:" + project.Id.Id;
        if (_baselineCache.TryGetValue(key, out var cached))
            return cached;
        var computed = await ForProjectAsync(project, cancellationToken).ConfigureAwait(false);
        _baselineCache[key] = computed;
        return computed;
    }

    /// <summary>Every error in the project, bound whole. Used when the set of files a change can reach is too large to enumerate.</summary>
    public async Task<IReadOnlyList<FuseDiagnostic>> ForProjectAsync(Project project, CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
            return [];
        var diagnostics = compilation.GetDiagnostics(cancellationToken).Where(IsError).ToList();
        var withAnalyzers = _analyzers.For(project, compilation);
        if (withAnalyzers is not null)
            diagnostics.AddRange((await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken).ConfigureAwait(false)).Where(IsError));
        return Distinct(diagnostics.Select(ToFuse).OfType<FuseDiagnostic>());
    }

    private async Task<IEnumerable<FuseDiagnostic>> ForDocumentAsync(Document document, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
            return [];
        var diagnostics = model.GetDiagnostics(cancellationToken: cancellationToken).Where(IsError).ToList();
        var withAnalyzers = _analyzers.For(document.Project, model.Compilation);
        if (withAnalyzers is not null)
        {
            diagnostics.AddRange((await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, cancellationToken).ConfigureAwait(false)).Where(IsError));
            diagnostics.AddRange((await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, cancellationToken).ConfigureAwait(false)).Where(IsError));
        }

        return diagnostics.Select(ToFuse).OfType<FuseDiagnostic>();
    }

    /// <summary>Errors in source-generated documents whose <c>#line</c> mappings point at <paramref name="path"/> (Razor components and views).</summary>
    private async Task<IEnumerable<FuseDiagnostic>> ForGeneratedFromAsync(Project project, string path, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(path);
        var result = new List<FuseDiagnostic>();
        foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
        {
            var text = await generated.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (!text.ToString().Contains(fileName, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var diagnostic in await ForDocumentAsync(generated, cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(diagnostic.Path, _root.Relative(path), StringComparison.OrdinalIgnoreCase))
                    result.Add(diagnostic);
            }
        }

        return result;
    }

    private static bool IsError(RoslynDiagnostic diagnostic) =>
        diagnostic.Severity == DiagnosticSeverity.Error && !diagnostic.IsSuppressed;

    private FuseDiagnostic? ToFuse(RoslynDiagnostic diagnostic)
    {
        if (!diagnostic.Location.IsInSource && diagnostic.Location.Kind != LocationKind.ExternalFile)
            return null;
        var span = diagnostic.Location.GetMappedLineSpan();
        if (!span.IsValid || string.IsNullOrEmpty(span.Path))
            return null;
        return new FuseDiagnostic(
            _root.Relative(span.Path),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            diagnostic.Id,
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Removes duplicates that come from compiling one file for several target frameworks.</summary>
    private static List<FuseDiagnostic> Distinct(IEnumerable<FuseDiagnostic> diagnostics) =>
        diagnostics.Distinct().OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.Column).ToList();
}
