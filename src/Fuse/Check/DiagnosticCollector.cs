using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Fuse.Check.Model;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Check;

/// <summary>
///     Computes the errors of one file (in every target framework it compiles for) or of whole projects, including
///     errors from analyzers that can report errors. Baseline results are cached until the baseline's content changes,
///     because it does not change between edits.
/// </summary>
internal sealed class DiagnosticCollector
{
    private readonly RepoRoot _root;
    private readonly AnalyzerSelector _analyzers;
    private readonly ConcurrentDictionary<RepoPath, IReadOnlyList<CompilerError>> _baselineFiles = new();
    private readonly ConcurrentDictionary<ProjectId, IReadOnlyList<CompilerError>> _baselineProjects = new();
    private readonly ConditionalWeakTable<CompilationWithAnalyzers, Task<ImmutableArray<Diagnostic>>> _compilationWide = new();
    private int _cachedGeneration = -1;

    private long _compilerTicks;
    private long _analyzerTicks;

    /// <summary>How the current check runs analyzers on the working tree; checks run one at a time.</summary>
    private AnalyzerRun _run = AnalyzerRun.Full;

    private int _unfinished;
    private readonly ConcurrentDictionary<string, byte> _leftOut = new(StringComparer.Ordinal);

    /// <summary>Starts a check that runs analyzers on the working tree as <paramref name="run"/> says.</summary>
    public void Begin(AnalyzerRun run)
    {
        _run = run;
        Interlocked.Exchange(ref _unfinished, 0);
        _leftOut.Clear();
        TakeTimings();
    }

    /// <summary>
    ///     What the check's analyzers did not do: how many files or projects their analyzers did not finish before the
    ///     deadline, and the compilation-wide analyzers the check left out.
    /// </summary>
    public (int Unfinished, IReadOnlyList<string> LeftOut) TakeAnalyzerReport() =>
        (Interlocked.Exchange(ref _unfinished, 0), [.. _leftOut.Keys.Order(StringComparer.Ordinal)]);

    /// <param name="root">The repository, for relative paths.</param>
    /// <param name="configurationGeneration">Changes whenever project configuration may have changed.</param>
    /// <param name="log">Where the analyzers' plans are written.</param>
    public DiagnosticCollector(RepoRoot root, Func<int> configurationGeneration, Action<string> log)
    {
        _root = root;
        _analyzers = new AnalyzerSelector(configurationGeneration, log);
    }

    /// <summary>Time spent binding and running analyzers since the last call, for the engine log.</summary>
    public (long CompilerMs, long AnalyzerMs) TakeTimings()
    {
        var compiler = Interlocked.Exchange(ref _compilerTicks, 0);
        var analyzer = Interlocked.Exchange(ref _analyzerTicks, 0);
        return (compiler * 1000 / System.Diagnostics.Stopwatch.Frequency, analyzer * 1000 / System.Diagnostics.Stopwatch.Frequency);
    }

    /// <summary>Errors reported in <paramref name="path"/>, from the regular documents and from Razor-generated code mapped back to it.</summary>
    public Task<IReadOnlyList<CompilerError>> ForFileAsync(Solution solution, RepoPath path, CancellationToken cancellationToken) =>
        ForFileAsync(solution, path, baseline: false, cancellationToken);

    private async Task<IReadOnlyList<CompilerError>> ForFileAsync(Solution solution, RepoPath path, bool baseline, CancellationToken cancellationToken)
    {
        var result = new List<CompilerError>();
        foreach (var id in solution.GetDocumentIdsWithFilePath(path.Absolute))
        {
            var document = solution.GetDocument(id);
            if (document is not null)
            {
                result.AddRange(await ForDocumentAsync(document, baseline, cancellationToken).ConfigureAwait(false));
                continue;
            }

            if (solution.GetAdditionalDocument(id) is { } additional)
                result.AddRange(await ForGeneratedFromAsync(solution.GetProject(additional.Project.Id)!, path, baseline, cancellationToken).ConfigureAwait(false));
        }

        return Distinct(result);
    }

    /// <summary>Same as <see cref="ForFileAsync(Solution, RepoPath, CancellationToken)"/> against the baseline, cached until the baseline changes.</summary>
    public async Task<IReadOnlyList<CompilerError>> ForBaselineFileAsync(Solution baseline, int generation, RepoPath path, CancellationToken cancellationToken)
    {
        ForgetOlderBaseline(generation);
        if (_baselineFiles.TryGetValue(path, out var cached))
            return cached;
        var computed = await ForFileAsync(baseline, path, baseline: true, cancellationToken).ConfigureAwait(false);
        _baselineFiles[path] = computed;
        return computed;
    }

    /// <summary>Same as <see cref="ForProjectAsync(Project, CancellationToken)"/> against the baseline, cached until the baseline changes.</summary>
    public async Task<IReadOnlyList<CompilerError>> ForBaselineProjectAsync(Project project, int generation, CancellationToken cancellationToken)
    {
        ForgetOlderBaseline(generation);
        if (_baselineProjects.TryGetValue(project.Id, out var cached))
            return cached;
        var computed = await ForProjectAsync(project, baseline: true, cancellationToken).ConfigureAwait(false);
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
    public Task<IReadOnlyList<CompilerError>> ForProjectAsync(Project project, CancellationToken cancellationToken) =>
        ForProjectAsync(project, baseline: false, cancellationToken);

    /// <remarks>
    ///     A working-tree project whose analyzers pass the deadline is answered with its compiler errors alone. Bound whole,
    ///     every analyzer runs, compilation-wide or not: the pass reads the whole compilation anyway.
    /// </remarks>
    private async Task<IReadOnlyList<CompilerError>> ForProjectAsync(Project project, bool baseline, CancellationToken cancellationToken)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        if (compilation is null)
            return [];
        // With analyzers, one pass produces compiler and analyzer diagnostics together. An analyzer's error is marked as
        // one by its id, so it equals the same error found when its file is bound alone, which a check of the targets
        // also reports.
        var withAnalyzers = _analyzers.For(project, compilation);
        if (withAnalyzers is null)
            return CompilerErrors(compilation, cancellationToken);
        var analyzerIds = withAnalyzers.Analyzers.SelectMany(a => a.SupportedDiagnostics).Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var deadline = baseline ? CancellationToken.None : _run.Deadline;
        using var analyzing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline);
        try
        {
            var diagnostics = await withAnalyzers.GetAllDiagnosticsAsync(analyzing.Token).ConfigureAwait(false);
            return Distinct(diagnostics.Where(IsError).Select(d => ToCompilerError(d, fromAnalyzer: analyzerIds.Contains(d.Id))).OfType<CompilerError>());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _unfinished);
            return CompilerErrors(compilation, cancellationToken);
        }
    }

    private List<CompilerError> CompilerErrors(Compilation compilation, CancellationToken cancellationToken) =>
        Distinct(compilation.GetDiagnostics(cancellationToken).Where(IsError).Select(d => ToCompilerError(d, fromAnalyzer: false)).OfType<CompilerError>());

    private async Task<IEnumerable<CompilerError>> ForDocumentAsync(Document document, bool baseline, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
            return [];

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var diagnostics = model.GetDiagnostics(cancellationToken: cancellationToken).Where(IsError).Select(d => (Roslyn: d, FromAnalyzer: false)).ToList();
        Interlocked.Add(ref _compilerTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
        diagnostics.AddRange((await RunAnalyzersAsync(document.Project, model, baseline ? AnalyzerRun.Full : _run, cancellationToken).ConfigureAwait(false)).Select(d => (Roslyn: d, FromAnalyzer: true)));

        return diagnostics.Select(d => ToCompilerError(d.Roslyn, d.FromAnalyzer)).OfType<CompilerError>();
    }

    /// <summary>
    ///     The analyzer errors in the file of <paramref name="model"/>: the per-file analyzers run on that file, and the
    ///     compilation-wide ones once over the whole compilation, whose result every file of the check shares. Past
    ///     <paramref name="run"/>'s deadline, the file keeps the analyzer errors found so far and counts as unfinished.
    /// </summary>
    private async Task<IEnumerable<Diagnostic>> RunAnalyzersAsync(Project project, SemanticModel model, AnalyzerRun run, CancellationToken cancellationToken)
    {
        var withAnalyzers = _analyzers.For(project, model.Compilation);
        if (withAnalyzers is null)
            return [];
        var result = new List<Diagnostic>();
        using var analyzing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Deadline);
        try
        {
            // The per-file part is timed here; the compilation-wide run, which every file waits for, times itself once.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            AnalyzerPlan plan;
            try
            {
                plan = await _analyzers.PlanAsync(project, withAnalyzers, model, analyzing.Token).ConfigureAwait(false);
                if (!plan.PerFile.IsEmpty)
                {
                    var syntax = withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, plan.PerFile, analyzing.Token);
                    var semantic = withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, plan.PerFile, analyzing.Token);
                    result.AddRange(await syntax.ConfigureAwait(false));
                    result.AddRange(await semantic.ConfigureAwait(false));
                }
            }
            finally
            {
                Interlocked.Add(ref _analyzerTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }

            if (!plan.CompilationWide.IsEmpty && !run.CompilationWide)
            {
                foreach (var (analyzer, _) in plan.CompilationWide)
                    _leftOut.TryAdd(AnalyzerSelector.Name(analyzer), 0);
            }
            else if (!plan.CompilationWide.IsEmpty)
            {
                var all = await CompilationWideAsync(withAnalyzers, plan, analyzing.Token).ConfigureAwait(false);
                result.AddRange(all.Where(d => d.Location.SourceTree == model.SyntaxTree));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _unfinished);
        }

        return result.Where(IsError);
    }

    /// <summary>
    ///     The diagnostics the compilation-wide analyzers report anywhere in the compilation, computed once per compilation:
    ///     the files of a check, and every later check until an edit makes a new compilation, share it. A run that was
    ///     cancelled or failed is not kept.
    /// </summary>
    private Task<ImmutableArray<Diagnostic>> CompilationWideAsync(CompilationWithAnalyzers analyzers, AnalyzerPlan plan, CancellationToken cancellationToken)
    {
        lock (_compilationWide)
        {
            if (_compilationWide.TryGetValue(analyzers, out var running) && !running.IsCanceled && !running.IsFaulted)
                return running;
            // Task.Run, so the analyzers' synchronous initialization runs outside the lock.
            var started = Task.Run(
                async () =>
                {
                    var timer = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        return (await analyzers.GetAnalysisResultAsync(plan.CompilationWideAnalyzers, cancellationToken).ConfigureAwait(false)).GetAllDiagnostics();
                    }
                    finally
                    {
                        Interlocked.Add(ref _analyzerTicks, System.Diagnostics.Stopwatch.GetTimestamp() - timer);
                    }
                },
                cancellationToken);
            _compilationWide.AddOrUpdate(analyzers, started);
            return started;
        }
    }

    /// <summary>Errors in source-generated documents whose <c>#line</c> mappings point at <paramref name="path"/> (Razor components and views).</summary>
    private async Task<IEnumerable<CompilerError>> ForGeneratedFromAsync(Project project, RepoPath path, bool baseline, CancellationToken cancellationToken)
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
            foreach (var diagnostic in await ForDocumentAsync(generated, baseline, cancellationToken).ConfigureAwait(false))
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
