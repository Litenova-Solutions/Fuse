using Fuse.Check.Analyzers;
using Fuse.Check.Model;
using Fuse.Check.Pipeline;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
namespace Fuse.Check.Diagnostics;

/// <summary>
///     Computes the errors of one file (in every target framework it compiles for) or of whole projects, including
///     errors from analyzers that can report errors. A whole project binds its compiler errors tree by tree and its
///     analyzer errors with one run over the whole compilation, partitioned back to files by their location's path.
///     Baseline results whose analyzers finished are kept in the disk store, so a baseline compilation
///     is built only on a store miss or for the files reach searches, and its analyzer runs are released when the
///     check that built them ends. What one check counts and how it runs analyzers live in its
///     <see cref="CheckContext"/>, so checks that run at the same time share the caches below and nothing else.
/// </summary>
internal sealed class DiagnosticCollector
{
    private readonly RepoRoot _root;
    private readonly AnalyzerSelector _analyzers;
    private readonly BaselineStore _store;
    private readonly DiagnosticCache _cache;
    private readonly AnalyzerExecution _execution;

    /// <summary>How many declaration fingerprints have been computed, as against read back from the state cache above.</summary>
    internal int DeclarationFingerprintComputations => _cache.DeclarationFingerprintComputations;

    /// <summary>The context of a caller that uses the calls without one, set by <see cref="Begin"/>.</summary>
    private readonly AsyncLocal<CheckContext?> _legacy = new();

    private readonly CheckContext _default = new(AnalyzerRun.Full, reuse: false);

    /// <param name="root">The repository, for relative paths.</param>
    /// <param name="configurationGeneration">Changes whenever project configuration may have changed.</param>
    /// <param name="log">Where the analyzers' plans are written.</param>
    public DiagnosticCollector(RepoRoot root, Func<int> configurationGeneration, Action<string> log)
    {
        _root = root;
        _analyzers = new AnalyzerSelector(configurationGeneration, log);
        _store = new BaselineStore(root, log);
        var analyses = new CompilationAnalyses();
        _cache = new DiagnosticCache(configurationGeneration, analyses);
        _execution = new AnalyzerExecution(_analyzers, analyses, root);
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
    public void BeginContext(CheckContext context) => _cache.BeginConfiguration(context);

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
                var additionalProject = solution.GetProject(additional.Project.Id) ?? throw new InvalidOperationException("solution has no project for the additional document.");
                var (errors, generatedFinished) = await ForGeneratedFromAsync(additionalProject, path, baseline, context, cancellationToken).ConfigureAwait(false);
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
        _cache.ForgetOlderBaseline(generation);
        var compilationWide = context.Run.CompilationWide;
        if (_cache.TryGetBaselineFile(generation, path, compilationWide, out var kept))
            return (kept, true);
        if (baselineKey is not null)
        {
            context.CountStoreLookup();
            if (_store.TryReadFile(baselineKey, path) is { } stored && (stored.CompilationWide || !compilationWide))
            {
                context.CountStoreHit();
                _cache.StoreBaselineFile(generation, path, stored.Errors, stored.CompilationWide);
                return (stored.Errors, true);
            }

            if (StoredProjectErrorsFor(baseline, baselineKey, path) is { } fromProject)
            {
                context.CountStoreHit();
                _cache.StoreBaselineFile(generation, path, fromProject, compilationWide: true);
                return (fromProject, true);
            }
        }

        var (errors, finished) = await ForFileAsync(baseline, path, baseline: true, context, cancellationToken).ConfigureAwait(false);
        if (finished)
        {
            _cache.StoreBaselineFile(generation, path, errors, compilationWide);
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
        _cache.ForgetOlderBaseline(generation);
        if (_cache.TryGetBaselineProject(generation, project.Id, out var kept))
            return (kept, true);
        var storedKey = baselineKey is null ? null : _store.ProjectKeyFor(project);
        if (baselineKey is not null && storedKey is not null)
        {
            context.CountStoreLookup();
            if (_store.TryReadProject(baselineKey, storedKey) is { } stored)
            {
                context.CountStoreHit();
                _cache.StoreBaselineProject(generation, project.Id, stored);
                return (stored, true);
            }
        }

        if (baselineKey is not null
            && await StoredFileErrorsForAsync(project, baselineKey, context.Run.CompilationWide, cancellationToken).ConfigureAwait(false) is { } fromFiles)
        {
            context.CountStoreHit();
            _cache.StoreBaselineProject(generation, project.Id, fromFiles);
            return (fromFiles, true);
        }

        var (errors, finished) = await ForProjectAsync(project, baseline: true, context, cancellationToken).ConfigureAwait(false);
        if (finished)
        {
            _cache.StoreBaselineProject(generation, project.Id, errors);
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

    /// <summary>
    ///     How long the warm's analyzers run for one project. The warm runs in the background lane, so a longer run
    ///     would hold the warm past the checks that need the store.
    /// </summary>
    internal static readonly TimeSpan WarmBudget = TimeSpan.FromSeconds(30);

    /// <summary>Same as the call without a context, for <paramref name="context"/>.</summary>
    /// <returns>True when the project has compilation-wide analyzers that were warmed.</returns>
    public async Task<bool> WarmBaselineProjectAsync(Project baselineProject, int generation, CheckContext context, CancellationToken shutdown)
    {
        _cache.ForgetOlderBaseline(generation);
        if (!_cache.IsCachedGeneration(generation))
            return false;
        using var budget = new CancellationTokenSource(WarmBudget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, budget.Token);
        try
        {
            var compilation = await baselineProject.GetCompilationAsync(linked.Token).ConfigureAwait(false);
            if (compilation is null)
                return false;
            var withAnalyzers = _analyzers.For(baselineProject, compilation);
            if (withAnalyzers is null)
                return false;
            if (await FirstModelAsync(baselineProject, linked.Token).ConfigureAwait(false) is not { } probe)
                return false;
            var plan = await _analyzers.PlanAsync(baselineProject, withAnalyzers, probe, linked.Token).ConfigureAwait(false);
            if (plan.CompilationWide.IsEmpty)
                return false;
            // The shutdown token stops the wait, never the run: a run outlives the warm that started it, as it outlives a check.
            await _execution.CompilationWideAsync(baselineProject, baseline: true, withAnalyzers, plan.CompilationWideAnalyzers, context, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!shutdown.IsCancellationRequested)
        {
            return false;
        }
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
        _cache.ForgetOlderBaseline(generation);
        if (!_cache.IsCachedGeneration(generation))
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

        using var budget = new CancellationTokenSource(WarmBudget);
        var budgeted = new CheckContext(new AnalyzerRun(context.Run.CompilationWide, budget.Token), context.Reuse);
        var (errors, finished) = await ForProjectAsync(baselineProject, baseline: true, budgeted, shutdown).ConfigureAwait(false);
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
        var paths = baselineProject.Documents.Select(d => d.FilePath).OfType<string>().Select(_root.PathOf).Distinct().ToList();
        _cache.ReleaseBaselineProject(baselineProject.Id, paths, generation);
    }

    /// <summary>
    ///     Releases the kept baseline results and the baseline analyzer runs, when the check that built them ends.
    ///     Runs a pinned check waits on stop when its last pin goes (<see cref="CompilationAnalyses"/>). The stored
    ///     results stay, so a later check reads HEAD's errors from the store.
    /// </summary>
    public void ReleaseBaseline() => _cache.ReleaseBaseline();

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

    /// <summary>Every error in the project, bound whole. Used when the set of files a change can reach is too large to enumerate.</summary>
    public async Task<IReadOnlyList<CompilerError>> ForProjectAsync(Project project, CancellationToken cancellationToken) =>
        (await ForProjectAsync(project, baseline: false, Legacy, cancellationToken).ConfigureAwait(false)).Errors;

    /// <summary>Every error in the project for <paramref name="context"/>, bound whole.</summary>
    public async Task<IReadOnlyList<CompilerError>> ForProjectAsync(Project project, CheckContext context, CancellationToken cancellationToken) =>
        (await ForProjectAsync(project, baseline: false, context, cancellationToken).ConfigureAwait(false)).Errors;

    /// <remarks>
    ///     The project binds its compiler errors tree by tree, keeping only one model at a time, and its analyzer
    ///     errors with one run over the whole compilation, partitioned back to files by their location's path.
    ///     Bound whole, every analyzer runs, compilation-wide or not. A project whose analyzers pass the deadline
    ///     is answered with the compiler errors the trees read, and counts as unfinished once, by its name. A
    ///     baseline whose analyzers did not finish leaves the working tree's analyzer errors out, as
    ///     <see cref="IntroducedErrors"/> decides from the finished flag.
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

        var withAnalyzers = _analyzers.For(project, compilation);
        var result = new List<CompilerError>();
        foreach (var document in documents)
        {
            context.CountDocumentBound();
            var updating = System.Diagnostics.Stopwatch.GetTimestamp();
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            context.AddUpdateTicks(System.Diagnostics.Stopwatch.GetTimestamp() - updating);
            if (model is null)
                continue;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            result.AddRange(model.GetDiagnostics(cancellationToken: cancellationToken).Where(IsError).Select(d => ToCompilerError(d, fromAnalyzer: false)).OfType<CompilerError>());
            context.AddCompilerTicks(System.Diagnostics.Stopwatch.GetTimestamp() - started);
        }

        if (withAnalyzers is null)
            return (Distinct(result), true);

        using var analyzing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.Run.Deadline);
        try
        {
            var all = await _execution.WholeProjectAsync(project, baseline, withAnalyzers, context, analyzing.Token).ConfigureAwait(false);
            result.AddRange(all.Where(IsError).Select(d => ToCompilerError(d, fromAnalyzer: true)).OfType<CompilerError>());
            return (Distinct(result), true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!baseline)
                context.MarkUnfinished(project.Name);
            return (Distinct(result), false);
        }
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
        var key = baseline || !context.Reuse || document is SourceGeneratedDocument ? null : await _cache.KeyAsync(document, context, cancellationToken).ConfigureAwait(false);
        if (key is not null && _cache.TryGetCurrentFile(document.Id, key, out var reused))
        {
            context.CountReused();
            var updating = System.Diagnostics.Stopwatch.GetTimestamp();
            var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            context.AddUpdateTicks(System.Diagnostics.Stopwatch.GetTimestamp() - updating);
            if (model is null)
                return (reused, true);
            var (compilationWide, _, compilationWideFinished) = await _execution.RunAsync(new AnalyzerExecutionRequest(document.Project, model, baseline, PerFile: false, context, wholeProject), cancellationToken).ConfigureAwait(false);
            return ([.. reused, .. compilationWide.Select(d => ToCompilerError(d.Diagnostic, fromAnalyzer: true)).OfType<CompilerError>()], compilationWideFinished);
        }

        var updated = System.Diagnostics.Stopwatch.GetTimestamp();
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        context.AddUpdateTicks(System.Diagnostics.Stopwatch.GetTimestamp() - updated);
        if (semanticModel is null)
            return ([], true);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var compiler = semanticModel.GetDiagnostics(cancellationToken: cancellationToken).Where(IsError).Select(d => ToCompilerError(d, fromAnalyzer: false)).OfType<CompilerError>().ToList();
        context.AddCompilerTicks(System.Diagnostics.Stopwatch.GetTimestamp() - started);
        var (fromAnalyzers, perFileFinished, finished) = await _execution.RunAsync(new AnalyzerExecutionRequest(document.Project, semanticModel, baseline, PerFile: true, context, wholeProject), cancellationToken).ConfigureAwait(false);
        var analyzerErrors = fromAnalyzers.Select(d => (Error: ToCompilerError(d.Diagnostic, fromAnalyzer: true), d.PerFile)).Where(d => d.Error is not null).ToList();
        if (key is not null && perFileFinished)
            _cache.StoreCurrentFile(document.Id, key, [.. compiler, .. analyzerErrors.Where(d => d.PerFile).Select(d => d.Error).OfType<CompilerError>()]);
        return ([.. compiler, .. analyzerErrors.Select(d => d.Error).OfType<CompilerError>()], finished);
    }

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
}
