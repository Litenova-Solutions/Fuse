using System.Collections.Concurrent;
using Fuse.Check.Model;
using Fuse.Paths;
using Microsoft.CodeAnalysis;

namespace Fuse.Check;

/// <summary>
///     Computes the errors of one file (in every target framework it compiles for) or of whole projects, including
///     errors from analyzers that can report errors. Baseline results are cached until the HEAD view's content changes,
///     because it does not change between edits.
/// </summary>
internal sealed class DiagnosticCollector
{
    private readonly RepoRoot _root;
    private readonly AnalyzerSelector _analyzers;
    private readonly ConcurrentDictionary<RepoPath, IReadOnlyList<CompilerError>> _baselineFiles = new();
    private readonly ConcurrentDictionary<ProjectId, IReadOnlyList<CompilerError>> _baselineProjects = new();
    private int _cachedGeneration = -1;

    private long _compilerTicks;
    private long _analyzerTicks;

    /// <param name="root">The repository, for relative paths.</param>
    /// <param name="configurationGeneration">Changes whenever project configuration may have changed.</param>
    public DiagnosticCollector(RepoRoot root, Func<int> configurationGeneration)
    {
        _root = root;
        _analyzers = new AnalyzerSelector(configurationGeneration);
    }

    /// <summary>Time spent binding and running analyzers since the last call, for the engine log.</summary>
    public (long CompilerMs, long AnalyzerMs) TakeTimings()
    {
        var compiler = Interlocked.Exchange(ref _compilerTicks, 0);
        var analyzer = Interlocked.Exchange(ref _analyzerTicks, 0);
        return (compiler * 1000 / System.Diagnostics.Stopwatch.Frequency, analyzer * 1000 / System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>Errors reported in <paramref name="path"/>, from the regular documents and from Razor-generated code mapped back to it.</summary>
    public async Task<IReadOnlyList<CompilerError>> ForFileAsync(Solution solution, RepoPath path, CancellationToken cancellationToken)
    {
        var result = new List<CompilerError>();
        foreach (var id in solution.GetDocumentIdsWithFilePath(path.Absolute))
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
    public async Task<IReadOnlyList<CompilerError>> ForBaselineFileAsync(Solution baseline, int generation, RepoPath path, CancellationToken cancellationToken)
    {
        ForgetOlderBaseline(generation);
        if (_baselineFiles.TryGetValue(path, out var cached))
            return cached;
        var computed = await ForFileAsync(baseline, path, cancellationToken).ConfigureAwait(false);
        _baselineFiles[path] = computed;
        return computed;
    }

    /// <summary>Same as <see cref="ForProjectAsync"/> against the baseline, cached until the baseline changes.</summary>
    public async Task<IReadOnlyList<CompilerError>> ForBaselineProjectAsync(Project project, int generation, CancellationToken cancellationToken)
    {
        ForgetOlderBaseline(generation);
        if (_baselineProjects.TryGetValue(project.Id, out var cached))
            return cached;
        var computed = await ForProjectAsync(project, cancellationToken).ConfigureAwait(false);
        _baselineProjects[project.Id] = computed;
        return computed;
    }

    /// <summary>Empties both baseline caches when <paramref name="generation"/> is not the baseline they were filled from.</summary>
    private void ForgetOlderBaseline(int generation)
    {
        if (generation == _cachedGeneration)
            return;
        _baselineFiles.Clear();
        _baselineProjects.Clear();
        _cachedGeneration = generation;
    }

    /// <summary>Every error in the project, bound whole. Used when the set of files a change can reach is too large to enumerate.</summary>
    public async Task<IReadOnlyList<CompilerError>> ForProjectAsync(Project project, CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
            return [];
        // With analyzers, one pass produces compiler and analyzer diagnostics together. An analyzer's error is marked as
        // one by its id, so it equals the same error found when its file is bound alone, which a check of the targets
        // also reports.
        var withAnalyzers = _analyzers.For(project, compilation);
        if (withAnalyzers is null)
            return Distinct(compilation.GetDiagnostics(cancellationToken).Where(IsError).Select(d => ToCompilerError(d, fromAnalyzer: false)).OfType<CompilerError>());
        var analyzerIds = withAnalyzers.Analyzers.SelectMany(a => a.SupportedDiagnostics).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var diagnostics = await withAnalyzers.GetAllDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        return Distinct(diagnostics.Where(IsError).Select(d => ToCompilerError(d, fromAnalyzer: analyzerIds.Contains(d.Id))).OfType<CompilerError>());
    }

    private async Task<IEnumerable<CompilerError>> ForDocumentAsync(Document document, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
            return [];

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var diagnostics = model.GetDiagnostics(cancellationToken: cancellationToken).Where(IsError).Select(d => (Roslyn: d, FromAnalyzer: false)).ToList();
        Interlocked.Add(ref _compilerTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        diagnostics.AddRange((await RunAnalyzersAsync(document.Project, model, cancellationToken).ConfigureAwait(false)).Select(d => (Roslyn: d, FromAnalyzer: true)));

        return diagnostics.Select(d => ToCompilerError(d.Roslyn, d.FromAnalyzer)).OfType<CompilerError>();
    }

    private async Task<IEnumerable<Diagnostic>> RunAnalyzersAsync(Project project, SemanticModel model, CancellationToken cancellationToken)
    {
        var withAnalyzers = _analyzers.For(project, model.Compilation);
        if (withAnalyzers is null)
            return [];
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var syntax = withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, cancellationToken);
        var semantic = withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, cancellationToken);
        var result = (await syntax.ConfigureAwait(false)).Concat(await semantic.ConfigureAwait(false)).Where(IsError).ToList();
        Interlocked.Add(ref _analyzerTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        return result;
    }

    /// <summary>Errors in source-generated documents whose <c>#line</c> mappings point at <paramref name="path"/> (Razor components and views).</summary>
    private async Task<IEnumerable<CompilerError>> ForGeneratedFromAsync(Project project, RepoPath path, CancellationToken cancellationToken)
    {
        var fileName = path.FileName;
        var result = new List<CompilerError>();
        foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false))
        {
            // A quick search of the generated text for the file's name, in any case, so a document that cannot map to
            // the file is not bound; the errors it maps are compared as paths below.
            var text = await generated.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (!text.ToString().Contains(fileName, StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var diagnostic in await ForDocumentAsync(generated, cancellationToken).ConfigureAwait(false))
            {
                if (_root.PathOf(diagnostic.Path) == path)
                    result.Add(diagnostic);
            }
        }

        return result;
    }

    private static bool IsError(Diagnostic diagnostic) =>
        diagnostic.Severity == DiagnosticSeverity.Error && !diagnostic.IsSuppressed;

    private CompilerError? ToCompilerError(Diagnostic diagnostic, bool fromAnalyzer)
    {
        if (!diagnostic.Location.IsInSource && diagnostic.Location.Kind != LocationKind.ExternalFile)
            return null;
        var span = diagnostic.Location.GetMappedLineSpan();
        if (!span.IsValid || string.IsNullOrEmpty(span.Path))
            return null;
        return new CompilerError(
            _root.PathOf(span.Path).Relative,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            diagnostic.Id,
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            fromAnalyzer);
    }

    /// <summary>Removes duplicates that come from compiling one file for several target frameworks.</summary>
    private static List<CompilerError> Distinct(IEnumerable<CompilerError> diagnostics) =>
        diagnostics.Distinct().OrderBy(d => d.Path, StringComparer.Ordinal).ThenBy(d => d.Line).ThenBy(d => d.Column).ToList();
}
