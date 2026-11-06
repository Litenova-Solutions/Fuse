using System.Collections.Concurrent;
using System.Collections.Immutable;
using Fuse.Check.Model;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Fuse.Check;

/// <summary>
///     Computes the errors of one file (in every target framework it compiles for) or of whole projects, including
///     errors from analyzers that can report errors. Whole projects bind document by document: a document's model is
///     kept only while its errors are read, and the compilation-wide analyzers keep their one run over the whole
///     compilation. Baseline results whose analyzers finished are kept in the disk store, so a baseline compilation
///     is built only on a store miss or for the files reach searches, and its analyzer runs are released when the
///     check that built them ends. What one check counts and how it runs analyzers live in its
///     <see cref="CheckContext"/>, so checks that run at the same time share the caches below and nothing else.
/// </summary>
internal sealed class DiagnosticCollector
{
    private readonly RepoRoot _root;
    private readonly AnalyzerSelector _analyzers;
    private readonly BaselineStore _store;
    private readonly ConcurrentDictionary<(int Generation, RepoPath Path), BaselineFile> _baselineFiles = new();
    private readonly ConcurrentDictionary<(int Generation, ProjectId Project), IReadOnlyList<CompilerError>> _baselineProjects = new();
    private readonly CompilationAnalyses _analyses = new();
    private readonly Lock _baselineLock = new();
    private volatile int _cachedGeneration = -1;
    private readonly Func<int> _configurationGeneration;

    /// <summary>The compiler and per-file analyzer errors of each working-tree file at the last check that bound it, by the key they were found under.</summary>
    private readonly ConcurrentDictionary<DocumentId, CachedFile> _currentFiles = new();

    private readonly Lock _configurationLock = new();
    private int _currentFilesGeneration = -1;

    /// <summary>The context of a caller that uses the calls without one, set by <see cref="Begin"/>.</summary>
    private readonly AsyncLocal<CheckContext?> _legacy = new();

    private readonly CheckContext _default = new(AnalyzerRun.Full, reuse: false);

    /// <param name="root">The repository, for relative paths.</param>
    /// <param name="configurationGeneration">Changes whenever project configuration may have changed.</param>
    /// <param name="log">Where the analyzers' plans are written.</param>
    public DiagnosticCollector(RepoRoot root, Func<int> configurationGeneration, Action<string> log)
    {
        _root = root;
        _configurationGeneration = configurationGeneration;
        _analyzers = new AnalyzerSelector(configurationGeneration, log);
        _store = new BaselineStore(root, log);
    }

    private CheckContext Legacy => _legacy.Value ?? _default;

    /// <summary>
    ///     Starts a check that runs analyzers as <paramref name="run"/> says, and reuses working-tree errors from an earlier
    ///     check when <paramref name="reuse"/> is true. A caller that runs checks at the same time creates a
    ///     <see cref="CheckContext"/> per check and passes it to the calls that take one instead.
    /// </summary>
    /// <param name="reuse">
    ///     True for a check of every change, which binds many files again after a small edit. The key needs Roslyn's
    ///     dependent semantic version, which costs more than it saves in a check of the files an edit just wrote, so a check
    ///     of named files neither reads nor fills the cache.
    /// </param>
    public void Begin(AnalyzerRun run, bool reuse)
    {
        var context = new CheckContext(run, reuse);
        _legacy.Value = context;
        BeginContext(context);
    }

    /// <summary>
    ///     Prepares the shared caches for <paramref name="context"/>'s configuration. Projects that loaded again have
    ///     new document ids, so what was kept for the old ones is dropped with them once, however many checks at the
    ///     same time see the new configuration, and the analyzer runs over their old compilations stop when no
    ///     pinned check waits on them.
    /// </summary>
    public void BeginContext(CheckContext context)
    {
        _ = context;
        var generation = _configurationGeneration();
        lock (_configurationLock)
        {
            if (generation == _currentFilesGeneration)
                return;
            _currentFiles.Clear();
            _currentFilesGeneration = generation;
            _analyses.StopAll(baseline: false);
            _analyses.StopAll(baseline: true);
        }
    }

    /// <summary>
    ///     What the check's analyzers did not do: how many files or projects their analyzers did not finish before the
    ///     deadline, and the compilation-wide analyzers the check left out.
    /// </summary>
    public (int Unfinished, IReadOnlyList<string> LeftOut) TakeAnalyzerReport() => Legacy.TakeAnalyzerReport();

    /// <summary>
    ///     Counts a file, by its relative path, or a project, by its name, among those whose analyzer errors the current
    ///     check may be missing.
    /// </summary>
    public void MarkUnfinished(string fileOrProject) => Legacy.MarkUnfinished(fileOrProject);

    /// <summary>How many stored baseline results the current check read, and how many it computed instead, for the engine log.</summary>
    public (int Stored, int Computed) TakeBaselineReport() => Legacy.TakeBaselineReport();

    /// <summary>How many working-tree files had their errors reused since the last call, for the engine log.</summary>
    public int TakeReused() => Legacy.TakeReused();

    /// <summary>Time spent binding and running analyzers since the last call, for the engine log.</summary>
    public (long CompilerMs, long AnalyzerMs) TakeTimings() => Legacy.TakeTimings();

    /// <summary>Time spent updating the compilation, binding and running analyzers since the last call, and how many documents were bound, for the engine log.</summary>
    public (long UpdateMs, long CompilerMs, long AnalyzerMs, int Documents) TakeBindParts() => Legacy.TakeBindParts();

    /// <summary>Errors reported in <paramref name="path"/>, from the regular documents and from Razor-generated code mapped back to it.</summary>
    public async Task<IReadOnlyList<CompilerError>> ForFileAsync(Solution solution, RepoPath path, CancellationToken cancellationToken) =>
        (await ForFileAsync(solution, path, baseline: false, Legacy, cancellationToken).ConfigureAwait(false)).Errors;

    /// <summary>Errors reported in <paramref name="path"/> for <paramref name="context"/>, from the regular documents and from Razor-generated code mapped back to it.</summary>
    public async Task<IReadOnlyList<CompilerError>> ForFileAsync(Solution solution, RepoPath path, CheckContext context, CancellationToken cancellationToken) =>
        (await ForFileAsync(solution, path, baseline: false, context, cancellationToken).ConfigureAwait(false)).Errors;

    /// <returns>The errors, and whether the analyzers the check runs all finished on the file.</returns>
    private async Task<(List<CompilerError> Errors, bool AnalyzersFinished)> ForFileAsync(Solution solution, RepoPath path, bool baseline, CheckContext context, CancellationToken cancellationToken)
    {
        var result = new List<CompilerError>();
        var finished = true;
        foreach (var id in solution.GetDocumentIdsWithFilePath(path.Absolute))
        {
            var document = solution.GetDocument(id);
            if (document is not null)
            {
                var (errors, documentFinished) = await ForDocumentAsync(document, baseline, context, cancellationToken).ConfigureAwait(false);
                result.AddRange(errors);
                finished &= documentFinished;
                continue;
            }

            if (solution.GetAdditionalDocument(id) is { } additional)
            {
                var (errors, generatedFinished) = await ForGeneratedFromAsync(solution.GetProject(additional.Project.Id)!, path, baseline, context, cancellationToken).ConfigureAwait(false);
                result.AddRange(errors);
                finished &= generatedFinished;
            }
        }

        return (Distinct(result), finished);
    }

    /// <summary>
    ///     Same as <see cref="ForFileAsync(Solution, RepoPath, CancellationToken)"/> against the baseline, with the analyzers
    ///     and the deadline the check runs on the working tree. A result whose analyzers finished is cached until the
    ///     baseline changes; one that ran the compilation-wide analyzers also serves a check that leaves them out.
    /// </summary>
    /// <param name="baselineKey">The store key of this baseline, or null when nothing is kept for it.</param>
    /// <returns>
    ///     HEAD's errors in the file, and whether its analyzers finished. When they did not, an analyzer error the working
    ///     tree has there cannot be told from one HEAD has.
    /// </returns>
    public Task<(IReadOnlyList<CompilerError> Errors, bool AnalyzersFinished)> ForBaselineFileAsync(Solution baseline, int generation, RepoPath path, CancellationToken cancellationToken, string? baselineKey = null) =>
        ForBaselineFileAsync(baseline, generation, path, Legacy, cancellationToken, baselineKey);

    /// <summary>Same as the call without a context, for <paramref name="context"/>.</summary>
    /// <param name="baselineKey">The store key of this baseline, or null when nothing is kept for it.</param>
    public async Task<(IReadOnlyList<CompilerError> Errors, bool AnalyzersFinished)> ForBaselineFileAsync(Solution baseline, int generation, RepoPath path, CheckContext context, CancellationToken cancellationToken, string? baselineKey = null)
    {
        ForgetOlderBaseline(generation);
        var compilationWide = context.Run.CompilationWide;
        if (_baselineFiles.TryGetValue((generation, path), out var cached) && (cached.CompilationWide || !compilationWide))
            return (cached.Errors, true);
        if (baselineKey is not null)
        {
            context.CountStoreLookup();
            if (_store.TryReadFile(baselineKey, path) is { } stored && (stored.CompilationWide || !compilationWide))
            {
                context.CountStoreHit();
                _baselineFiles[(generation, path)] = new BaselineFile(stored.Errors, stored.CompilationWide);
                return (stored.Errors, true);
            }

            if (StoredProjectErrorsFor(baseline, baselineKey, path) is { } fromProject)
            {
                context.CountStoreHit();
                _baselineFiles[(generation, path)] = new BaselineFile(fromProject, CompilationWide: true);
                return (fromProject, true);
            }
        }

        var (errors, finished) = await ForFileAsync(baseline, path, baseline: true, context, cancellationToken).ConfigureAwait(false);
        if (finished)
        {
            _baselineFiles[(generation, path)] = new BaselineFile(errors, compilationWide);
            if (baselineKey is not null)
                _store.WriteFile(baselineKey, path, errors, compilationWide);
        }

        return (errors, finished);
    }

    /// <summary>
    ///     HEAD's stored errors in <paramref name="path"/> from the stored results of the projects that compile it, or
    ///     null when none of those projects is stored. The stored project results hold the errors of every file, so a
    ///     file reads its own from them without a baseline compilation.
    /// </summary>
    private List<CompilerError>? StoredProjectErrorsFor(Solution baseline, string baselineKey, RepoPath path)
    {
        var result = new List<CompilerError>();
        var stored = false;
        foreach (var id in baseline.GetDocumentIdsWithFilePath(path.Absolute))
        {
            if (baseline.GetProject(id.ProjectId) is not { } project || _store.ProjectKeyFor(project) is not { } storedKey)
                continue;
            if (_store.TryReadProject(baselineKey, storedKey) is not { } errors)
                continue;
            stored = true;
            result.AddRange(errors.Where(e => e.Path == path.Relative));
        }

        return stored ? Distinct(result) : null;
    }

    /// <summary>
    ///     HEAD's stored errors in every file of <paramref name="project"/> from its stored file results, or null when
    ///     a file of the project has none. A project with generated documents is left out: its generated errors are
    ///     in no file result of a document the project holds.
    /// </summary>
    private async Task<IReadOnlyList<CompilerError>?> StoredFileErrorsForAsync(Project project, string baselineKey, bool compilationWide, CancellationToken cancellationToken)
    {
        if ((await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false)).Any())
            return null;
        var paths = project.Documents.Select(d => d.FilePath).OfType<string>().Select(_root.PathOf).Distinct().ToList();
        if (paths.Count == 0)
            return null;
        var result = new List<CompilerError>();
        foreach (var path in paths)
        {
            if (_store.TryReadFile(baselineKey, path) is not { } stored || (compilationWide && !stored.CompilationWide))
                return null;
            result.AddRange(stored.Errors);
        }

        return Distinct(result);
    }

    /// <summary>
    ///     Same as <see cref="ForProjectAsync(Project, CancellationToken)"/> against the baseline, under the check's
    ///     deadline. A result whose analyzers finished is cached until the baseline changes.
    /// </summary>
    /// <param name="baselineKey">The store key of this baseline, or null when nothing is kept for it.</param>
    /// <returns>HEAD's errors in the project, and whether its analyzers finished.</returns>
    public Task<(IReadOnlyList<CompilerError> Errors, bool AnalyzersFinished)> ForBaselineProjectAsync(Project project, int generation, CancellationToken cancellationToken, string? baselineKey = null) =>
        ForBaselineProjectAsync(project, generation, Legacy, cancellationToken, baselineKey);

    /// <summary>Same as the call without a context, for <paramref name="context"/>.</summary>
    /// <param name="baselineKey">The store key of this baseline, or null when nothing is kept for it.</param>
    public async Task<(IReadOnlyList<CompilerError> Errors, bool AnalyzersFinished)> ForBaselineProjectAsync(Project project, int generation, CheckContext context, CancellationToken cancellationToken, string? baselineKey = null)
    {
        ForgetOlderBaseline(generation);
        if (_baselineProjects.TryGetValue((generation, project.Id), out var cached))
            return (cached, true);
        var storedKey = baselineKey is null ? null : _store.ProjectKeyFor(project);
        if (baselineKey is not null && storedKey is not null)
        {
            context.CountStoreLookup();
            if (_store.TryReadProject(baselineKey, storedKey) is { } stored)
            {
                context.CountStoreHit();
                _baselineProjects[(generation, project.Id)] = stored;
                return (stored, true);
            }
        }

        if (baselineKey is not null
            && await StoredFileErrorsForAsync(project, baselineKey, context.Run.CompilationWide, cancellationToken).ConfigureAwait(false) is { } fromFiles)
        {
            context.CountStoreHit();
            _baselineProjects[(generation, project.Id)] = fromFiles;
            return (fromFiles, true);
        }

        var (errors, finished) = await ForProjectAsync(project, baseline: true, context, cancellationToken).ConfigureAwait(false);
        if (finished)
        {
            _baselineProjects[(generation, project.Id)] = errors;
            if (baselineKey is not null && storedKey is not null)
                _store.WriteProject(baselineKey, storedKey, errors);
        }

        return (errors, finished);
    }

    /// <summary>
    ///     Runs the compilation-wide analyzers over <paramref name="baselineProject"/>, for the background warm after HEAD
    ///     moved, so the first check that needs their result finds it running or done instead of starting it. It fills the
    ///     same shared runs a check waits for (<see cref="CompilationAnalyses"/>); a check of the same compilation waits
    ///     for them instead of starting its own.
    /// </summary>
    /// <returns>True when the project has compilation-wide analyzers that were warmed.</returns>
    public Task<bool> WarmBaselineProjectAsync(Project baselineProject, int generation, CancellationToken shutdown) =>
        WarmBaselineProjectAsync(baselineProject, generation, Legacy, shutdown);

    /// <summary>Same as the call without a context, for <paramref name="context"/>.</summary>
    /// <returns>True when the project has compilation-wide analyzers that were warmed.</returns>
    public async Task<bool> WarmBaselineProjectAsync(Project baselineProject, int generation, CheckContext context, CancellationToken shutdown)
    {
        ForgetOlderBaseline(generation);
        if (generation != _cachedGeneration)
            return false;
        var compilation = await baselineProject.GetCompilationAsync(shutdown).ConfigureAwait(false);
        if (compilation is null)
            return false;
        var withAnalyzers = _analyzers.For(baselineProject, compilation);
        if (withAnalyzers is null)
            return false;
        if (await FirstModelAsync(baselineProject, shutdown).ConfigureAwait(false) is not { } probe)
            return false;
        var plan = await _analyzers.PlanAsync(baselineProject, withAnalyzers, probe, shutdown).ConfigureAwait(false);
        if (plan.CompilationWide.IsEmpty)
            return false;
        // The shutdown token stops the wait, never the run: a run outlives the warm that started it, as it outlives a check.
        await CompilationWideAsync(baselineProject, baseline: true, withAnalyzers, plan.CompilationWideAnalyzers, context, shutdown).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     Fills the disk store with HEAD's errors in <paramref name="baselineProject"/>, for the background warm
    ///     after HEAD moved, and releases the baseline compilation it built. The errors are stored per file and per
    ///     project, so the first check that needs them reads the store instead of binding HEAD. A project whose
    ///     stored results are already in the store is left alone, and no baseline compilation is built for it.
    /// </summary>
    /// <param name="baselineKey">The store key of this baseline, or null when nothing is kept for it.</param>
    /// <returns>True when the project has compilation-wide analyzers whose results were stored.</returns>
    public Task<bool> FillBaselineStoreAsync(Project baselineProject, int generation, CancellationToken shutdown, string? baselineKey = null) =>
        FillBaselineStoreAsync(baselineProject, generation, Legacy, shutdown, baselineKey);

    /// <summary>Same as the call without a context, for <paramref name="context"/>.</summary>
    /// <param name="baselineKey">The store key of this baseline, or null when nothing is kept for it.</param>
    /// <returns>True when the project has compilation-wide analyzers whose results were stored.</returns>
    public async Task<bool> FillBaselineStoreAsync(Project baselineProject, int generation, CheckContext context, CancellationToken shutdown, string? baselineKey = null)
    {
        ForgetOlderBaseline(generation);
        if (generation != _cachedGeneration)
            return false;
        if (baselineKey is not null && StoreHoldsProject(baselineProject, baselineKey, context.Run.CompilationWide))
            return false;

        var compilationWide = false;
        var compilation = await baselineProject.GetCompilationAsync(shutdown).ConfigureAwait(false);
        if (compilation is not null && _analyzers.For(baselineProject, compilation) is { } withAnalyzers
            && await FirstModelAsync(baselineProject, shutdown).ConfigureAwait(false) is { } probe)
        {
            var plan = await _analyzers.PlanAsync(baselineProject, withAnalyzers, probe, shutdown).ConfigureAwait(false);
            compilationWide = !plan.CompilationWide.IsEmpty;
        }

        var (errors, finished) = await ForProjectAsync(baselineProject, baseline: true, context, shutdown).ConfigureAwait(false);
        if (finished && baselineKey is not null)
        {
            foreach (var path in baselineProject.Documents.Select(d => d.FilePath).OfType<string>().Select(_root.PathOf).Distinct())
                _store.WriteFile(baselineKey, path, [.. errors.Where(e => e.Path == path.Relative)], context.Run.CompilationWide);
            if (_store.ProjectKeyFor(baselineProject) is { } storedKey)
                _store.WriteProject(baselineKey, storedKey, errors);
        }

        ReleaseBaselineProject(baselineProject, generation);
        return compilationWide && finished;
    }

    /// <summary>
    ///     True when the store already holds a finished result for every file of <paramref name="project"/>, or the
    ///     stored result of the project itself, so filling it would bind HEAD for nothing.
    /// </summary>
    private bool StoreHoldsProject(Project project, string baselineKey, bool compilationWide)
    {
        if (_store.ProjectKeyFor(project) is { } storedKey && _store.TryReadProject(baselineKey, storedKey) is not null)
            return true;
        var paths = project.Documents.Select(d => d.FilePath).OfType<string>().Select(_root.PathOf).Distinct().ToList();
        return paths.Count > 0
            && paths.All(path => _store.TryReadFile(baselineKey, path) is { } stored && (stored.CompilationWide || !compilationWide));
    }

    /// <summary>
    ///     Releases what a fill or a check built over the baseline of <paramref name="baselineProject"/>: its kept
    ///     baseline results go, and its analyzer runs stop when no pinned check waits on them. The stored results
    ///     stay, so the next check reads them from the store instead of binding HEAD again.
    /// </summary>
    public void ReleaseBaselineProject(Project baselineProject, int generation)
    {
        _baselineProjects.TryRemove((generation, baselineProject.Id), out _);
        foreach (var path in baselineProject.Documents.Select(d => d.FilePath).OfType<string>().Select(_root.PathOf).Distinct())
            _baselineFiles.TryRemove((generation, path), out _);
        _analyses.Release(baselineProject.Id, baseline: true);
    }

    /// <summary>
    ///     Releases the kept baseline results and the baseline analyzer runs, when the check that built them ends.
    ///     Runs a pinned check waits on stop when its last pin goes (<see cref="CompilationAnalyses"/>). The stored
    ///     results stay, so a later check reads HEAD's errors from the store.
    /// </summary>
    public void ReleaseBaseline()
    {
        _baselineFiles.Clear();
        _baselineProjects.Clear();
        _analyses.StopAll(baseline: true);
    }

    /// <summary>The semantic model of the project's first document, as the probe that plans its analyzers.</summary>
    private static async Task<SemanticModel?> FirstModelAsync(Project project, CancellationToken cancellationToken)
    {
        foreach (var document in project.Documents)
        {
            if (await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is { } model)
                return model;
        }

        return null;
    }

    /// <summary>
    ///     Drops the baseline results of generations older than <paramref name="generation"/>, and stops the analyzer
    ///     runs over the baseline when no pinned check waits on them. A generation older than the cached one is a warm
    ///     that started before HEAD moved again, which must not drop what a newer baseline already holds or stop its
    ///     runs. Results are keyed by their generation, so a check pinned to an older baseline computes its own again
    ///     after a drop instead of taking the newer baseline's.
    /// </summary>
    private void ForgetOlderBaseline(int generation)
    {
        if (generation <= _cachedGeneration)
            return;
        lock (_baselineLock)
        {
            if (generation <= _cachedGeneration)
                return;
            foreach (var key in _baselineFiles.Keys.Where(k => k.Generation < generation).ToList())
                _baselineFiles.TryRemove(key, out _);
            foreach (var key in _baselineProjects.Keys.Where(k => k.Generation < generation).ToList())
                _baselineProjects.TryRemove(key, out _);
            _analyses.StopAll(baseline: true);
            _cachedGeneration = generation;
        }
    }

    /// <summary>Every error in the project, bound whole. Used when the set of files a change can reach is too large to enumerate.</summary>
    public async Task<IReadOnlyList<CompilerError>> ForProjectAsync(Project project, CancellationToken cancellationToken) =>
        (await ForProjectAsync(project, baseline: false, Legacy, cancellationToken).ConfigureAwait(false)).Errors;

    /// <summary>Every error in the project for <paramref name="context"/>, bound whole.</summary>
    public async Task<IReadOnlyList<CompilerError>> ForProjectAsync(Project project, CheckContext context, CancellationToken cancellationToken) =>
        (await ForProjectAsync(project, baseline: false, context, cancellationToken).ConfigureAwait(false)).Errors;

    /// <remarks>
    ///     The project binds document by document: each document's model is kept only while its errors are read, so
    ///     a project with many files never holds a model for every file at once. The compilation-wide analyzers keep
    ///     their one run over the whole compilation, which every document of the project shares. Bound whole, every
    ///     analyzer runs, compilation-wide or not. A project whose analyzers pass the deadline is answered with the
    ///     compiler errors the stream read, and counts as unfinished once, by its name.
    /// </remarks>
    /// <returns>The project's errors, and whether its analyzers finished.</returns>
    private async Task<(IReadOnlyList<CompilerError> Errors, bool AnalyzersFinished)> ForProjectAsync(Project project, bool baseline, CheckContext context, CancellationToken cancellationToken)
    {
        var updated = System.Diagnostics.Stopwatch.GetTimestamp();
        var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        context.AddUpdateTicks(System.Diagnostics.Stopwatch.GetTimestamp() - updated);
        if (compilation is null)
            return ([], true);

        var documents = project.Documents.ToList();
        documents.AddRange(await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false));
        if (documents.Count == 0)
            return (CompilerErrors(compilation, cancellationToken), true);

        var result = new List<CompilerError>();
        var finished = true;
        foreach (var document in documents)
        {
            // The model of this document is the only one the stream holds; the next iteration drops it.
            var (errors, documentFinished) = await ForDocumentAsync(document, baseline, context, cancellationToken, wholeProject: true).ConfigureAwait(false);
            result.AddRange(errors);
            finished &= documentFinished;
        }

        if (finished && _analyzers.For(project, compilation) is { } withAnalyzers
            && await FirstModelAsync(project, cancellationToken).ConfigureAwait(false) is { } probe)
        {
            var plan = await _analyzers.PlanAsync(project, withAnalyzers, probe, cancellationToken).ConfigureAwait(false);
            if (!plan.CompilationWide.IsEmpty)
            {
                var all = await CompilationWideAsync(project, baseline, withAnalyzers, plan.CompilationWideAnalyzers, context, cancellationToken).ConfigureAwait(false);
                result.AddRange(all.Where(d => d.Location.SourceTree is null && IsError(d)).Select(d => ToCompilerError(d, fromAnalyzer: true)).OfType<CompilerError>());
            }
        }

        return (Distinct(result), finished);
    }

    private List<CompilerError> CompilerErrors(Compilation compilation, CancellationToken cancellationToken) =>
        Distinct(compilation.GetDiagnostics(cancellationToken).Where(IsError).Select(d => ToCompilerError(d, fromAnalyzer: false)).OfType<CompilerError>());

    /// <returns>The document's errors, and whether the analyzers the check runs all finished on it.</returns>
    private async Task<(IReadOnlyList<CompilerError> Errors, bool AnalyzersFinished)> ForDocumentAsync(Document document, bool baseline, CheckContext context, CancellationToken cancellationToken, bool wholeProject = false)
    {
        // A working-tree file whose text and whose project's declarations are as they were at an earlier check has the same
        // compiler and per-file analyzer errors, so they are reused; the compilation-wide analyzers read every file's
        // bodies, so their part is never reused this way.
        context.CountDocumentBound();
        var key = baseline || !context.Reuse || document is SourceGeneratedDocument ? null : await KeyAsync(document, cancellationToken).ConfigureAwait(false);
        if (key is not null && _currentFiles.TryGetValue(document.Id, out var cached) && cached.Key == key)
        {
            context.CountReused();
            var updating = System.Diagnostics.Stopwatch.GetTimestamp();
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            context.AddUpdateTicks(System.Diagnostics.Stopwatch.GetTimestamp() - updating);
            if (model is null)
                return (cached.Errors, true);
            var (compilationWide, _, compilationWideFinished) = await RunAnalyzersAsync(document.Project, model, baseline, perFile: false, context, cancellationToken, wholeProject).ConfigureAwait(false);
            return ([.. cached.Errors, .. compilationWide.Select(d => ToCompilerError(d.Diagnostic, fromAnalyzer: true)).OfType<CompilerError>()], compilationWideFinished);
        }

        var updated = System.Diagnostics.Stopwatch.GetTimestamp();
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        context.AddUpdateTicks(System.Diagnostics.Stopwatch.GetTimestamp() - updated);
        if (semanticModel is null)
            return ([], true);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var compiler = semanticModel.GetDiagnostics(cancellationToken: cancellationToken).Where(IsError).Select(d => ToCompilerError(d, fromAnalyzer: false)).OfType<CompilerError>().ToList();
        context.AddCompilerTicks(System.Diagnostics.Stopwatch.GetTimestamp() - started);
        var (fromAnalyzers, perFileFinished, finished) = await RunAnalyzersAsync(document.Project, semanticModel, baseline, perFile: true, context, cancellationToken, wholeProject).ConfigureAwait(false);
        var analyzerErrors = fromAnalyzers.Select(d => (Error: ToCompilerError(d.Diagnostic, fromAnalyzer: true), d.PerFile)).Where(d => d.Error is not null).ToList();
        if (key is not null && perFileFinished)
            _currentFiles[document.Id] = new CachedFile(key, [.. compiler, .. analyzerErrors.Where(d => d.PerFile).Select(d => d.Error!)]);
        return ([.. compiler, .. analyzerErrors.Select(d => d.Error!)], finished);
    }

    /// <summary>
    ///     What a working-tree file's compiler and per-file analyzer errors depend on: its text, the declarations of its
    ///     project and of the projects it references (Roslyn's dependent semantic version, which a change inside a method
    ///     body leaves as it is), and the project configuration.
    /// </summary>
    private async Task<FileKey> KeyAsync(Document document, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var declarations = await document.Project.GetDependentSemanticVersionAsync(cancellationToken).ConfigureAwait(false);
        return new FileKey(Convert.ToHexString(text.GetChecksum().AsSpan()), declarations, _configurationGeneration());
    }

    /// <summary>
    ///     The analyzer errors in the file of <paramref name="model"/>: the per-file analyzers run on that file when
    ///     <paramref name="perFile"/> is true, and the compilation-wide ones once over the whole compilation, whose result
    ///     every file of the check shares. The baseline runs the same analyzers as the working tree. Past the check's
    ///     deadline, the file keeps the analyzer errors found so far, and a working-tree file counts as unfinished.
    /// </summary>
    /// <returns>
    ///     Each error with whether a per-file analyzer reported it, whether the per-file analyzers finished, and whether
    ///     every analyzer the check runs finished.
    /// </returns>
    private async Task<(List<(Diagnostic Diagnostic, bool PerFile)> Errors, bool PerFileFinished, bool Finished)> RunAnalyzersAsync(
        Project project, SemanticModel model, bool baseline, bool perFile, CheckContext context, CancellationToken cancellationToken, bool wholeProject = false)
    {
        var withAnalyzers = _analyzers.For(project, model.Compilation);
        if (withAnalyzers is null)
            return ([], true, true);
        var run = context.Run;
        var result = new List<(Diagnostic Diagnostic, bool PerFile)>();
        var perFileFinished = false;
        var finished = false;
        using var analyzing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, run.Deadline);
        try
        {
            // The per-file part is timed here; the compilation-wide run, which every file waits for, times itself once.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            AnalyzerPlan plan;
            try
            {
                plan = await _analyzers.PlanAsync(project, withAnalyzers, model, analyzing.Token).ConfigureAwait(false);
                if (perFile && !plan.PerFile.IsEmpty)
                {
                    var syntax = withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(model.SyntaxTree, plan.PerFile, analyzing.Token);
                    var semantic = withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(model, filterSpan: null, plan.PerFile, analyzing.Token);
                    result.AddRange((await syntax.ConfigureAwait(false)).Select(d => (d, true)));
                    result.AddRange((await semantic.ConfigureAwait(false)).Select(d => (d, true)));
                }

                perFileFinished = true;
            }
            finally
            {
                context.AddAnalyzerTicks(System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }

            if (!plan.CompilationWide.IsEmpty && !run.CompilationWide && !wholeProject)
            {
                foreach (var (analyzer, _) in plan.CompilationWide)
                    context.MarkLeftOut(AnalyzerSelector.Name(analyzer));
                // This compilation is the project's newest, so a run an earlier check left going over an older one stops when no pinned check waits on it.
                _analyses.StopOlder(project.Id, baseline, withAnalyzers);
            }
            else if (!plan.CompilationWide.IsEmpty)
            {
                var all = await CompilationWideAsync(project, baseline, withAnalyzers, plan.CompilationWideAnalyzers, context, analyzing.Token).ConfigureAwait(false);
                result.AddRange(all.Where(d => d.Location.SourceTree == model.SyntaxTree).Select(d => (d, false)));
            }

            finished = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!baseline)
                context.MarkUnfinished(wholeProject ? project.Name : _root.PathOf(model.SyntaxTree.FilePath).Relative);
        }

        return ([.. result.Where(d => IsError(d.Diagnostic))], perFileFinished, finished);
    }

    /// <summary>
    ///     The diagnostics the compilation-wide analyzers report anywhere in the compilation, computed once per compilation:
    ///     the files of a check, and every later check until an edit makes a new compilation, share it, and it runs on past
    ///     the deadline of the check that started it (<see cref="CompilationAnalyses"/>).
    /// </summary>
    private Task<ImmutableArray<Diagnostic>> CompilationWideAsync(Project project, bool baseline, CompilationWithAnalyzers analyzers, ImmutableArray<DiagnosticAnalyzer> wide, CheckContext context, CancellationToken wait) =>
        _analyses.RunAsync(
            project.Id,
            baseline,
            analyzers,
            everyAnalyzer: false,
            async stop =>
            {
                var timer = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    return (await analyzers.GetAnalysisResultAsync(wide, stop).ConfigureAwait(false)).GetAllDiagnostics();
                }
                finally
                {
                    context.AddAnalyzerTicks(System.Diagnostics.Stopwatch.GetTimestamp() - timer);
                }
            },
            context,
            wait);

    /// <summary>Errors in source-generated documents whose <c>#line</c> mappings point at <paramref name="path"/> (Razor components and views).</summary>
    /// <returns>The errors, and whether the analyzers the check runs all finished on the documents.</returns>
    private async Task<(List<CompilerError> Errors, bool AnalyzersFinished)> ForGeneratedFromAsync(Project project, RepoPath path, bool baseline, CheckContext context, CancellationToken cancellationToken)
    {
        var fileName = path.FileName;
        var result = new List<CompilerError>();
        var finished = true;
        var updated = System.Diagnostics.Stopwatch.GetTimestamp();
        var generatedDocuments = await project.GetSourceGeneratedDocumentsAsync(cancellationToken).ConfigureAwait(false);
        context.AddUpdateTicks(System.Diagnostics.Stopwatch.GetTimestamp() - updated);
        foreach (var generated in generatedDocuments)
        {
            // A quick search of the generated text for the file's name, in any case, so a document that cannot map to
            // the file is not bound; the errors it maps are compared as paths below.
            var text = await generated.GetTextAsync(cancellationToken).ConfigureAwait(false);
            if (!text.ToString().Contains(fileName, StringComparison.OrdinalIgnoreCase))
                continue;
            var (errors, generatedFinished) = await ForDocumentAsync(generated, baseline, context, cancellationToken).ConfigureAwait(false);
            finished &= generatedFinished;
            foreach (var diagnostic in errors)
            {
                if (_root.PathOf(diagnostic.Path) == path)
                    result.Add(diagnostic);
            }
        }

        return (result, finished);
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

    /// <summary>HEAD's errors in one file from a run whose analyzers finished, and whether that run included the compilation-wide analyzers.</summary>
    private sealed record BaselineFile(IReadOnlyList<CompilerError> Errors, bool CompilationWide);

    /// <summary>What a working-tree file's errors were found under: its text's checksum, its dependent semantic version and the configuration generation.</summary>
    private sealed record FileKey(string Checksum, VersionStamp Declarations, int Configuration);

    /// <summary>A working-tree file's compiler and per-file analyzer errors, and the key they were found under.</summary>
    private sealed record CachedFile(FileKey Key, IReadOnlyList<CompilerError> Errors);
}
