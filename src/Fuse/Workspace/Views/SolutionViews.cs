using Fuse.Graph;
using Fuse.Paths;
using Fuse.Repo.Tracking;
using Fuse.Workspace.Blueprints;
using Fuse.Workspace.Generations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Workspace.Views;

/// <summary>
///     The two views of the loaded projects: <see cref="Current"/> (the working tree) and <see cref="Baseline"/> (the
///     same projects with every changed source restored to its HEAD content; project files are evaluated as they are on
///     disk). Both are derived from the loader's solution, and a file's content is applied to them one path at a time.
/// </summary>
/// <remarks>
///     Both views are immutable <see cref="Solution"/> snapshots, replaced whole, so a request reads a pair that belongs
///     together. The baseline's content only changes when HEAD moves or projects reload, so diagnostics computed from it
///     stay cached across checks. In each view a reference to a project outside the change cone (the projects that own
///     changed files, and the dependents a declaration change reaches) is supplied by that project's summary when one
///     is kept (<see cref="SummaryCache"/>), so binding a project in the cone does not build the compilation of every
///     project it references. The referenced project stays in the view with its documents, so reach, coverage and
///     test selection keep reading its documents; a project with no summary kept binds from source exactly as it
///     would without one. Summary use is opportunistic: a view is supplied by a summary only when that summary is
///     already kept, never by emitting one here, and a project starts being supplied by a summary only when the
///     views are derived from the loader again (projects load, reload or HEAD moves), never on a sync. A project
///     that enters the cone stops being supplied at the next sync, because a check of it binds source. Emitting
///     happens off the request path, in the background warm (<see cref="EmitSummaryAsync"/>), never under the
///     writer lock.
/// </remarks>
internal sealed class SolutionViews
{
    private readonly RepoRoot _root;
    private readonly ChangeTracker _tracker;
    private readonly ProjectLoader _projects;
    private readonly AnalyzerShadow _analyzers;
    private readonly SummaryCache _summaries;
    private readonly ViewDocumentPatcher _patcher;
    private readonly ViewSummaryApplicator _applicator;

    /// <summary>
    ///     The views with some files at HEAD content that are kept for sessions. A patch applies its files to each of
    ///     them, a rebuild derives each of them again, and <see cref="ClearSessionViews"/> sheds them, so a view a
    ///     session's check reads is the view the last sync left, with its compilations carried forward.
    /// </summary>
    private readonly SessionViews _sessionViews = new();

    /// <summary>The most session views kept; past it the view used least recently goes, and a later session derives it again from the current view, which answers the same.</summary>
    internal const int MaxSessionViews = SessionViews.MaxKept;

    /// <summary>The working tree and the baseline before summaries, set together.</summary>
    private sealed record SourcePair(Solution Current, Solution Baseline);

    private SourcePair? _sources;

    /// <summary>The source views, which are set once a rebuild or a clear has derived them.</summary>
    /// <exception cref="InvalidOperationException">The views are not derived yet.</exception>
    private SourcePair Sources => _sources ?? throw new InvalidOperationException("The views are not derived yet. Rebuild the views before reading them.");

    /// <summary>The most paths the touched set holds. Past it the next sync re-evaluates the projects and resets the set, so it stays bounded however long the engine runs.</summary>
    internal const int MaxTouchedPaths = 2000;

    /// <summary>
    ///     The loader's solution both views were last derived from, or null after <see cref="Clear"/>. It is set only
    ///     once a <see cref="RebuildAsync"/> has finished, so a rebuild that was cut short is done again.
    /// </summary>
    private Solution? _derivedFrom;

    /// <summary>
    ///     The HEAD the baseline's content was read at, by the last <see cref="Clear"/> or <see cref="RebuildAsync"/>. A
    ///     rebuild at another HEAD reads other content for every changed file, so it increments
    ///     <see cref="BaselineGeneration"/>.
    /// </summary>
    private string? _baselineHead;

    /// <summary>Projects shed by <see cref="ClearIdleConeProjects"/> so syncs keep them unloaded until requested again.</summary>
    private readonly HashSet<RepoPath> _shedProjects = [];

    /// <summary>True when <see cref="ClearHeadCompilations"/> has dropped the baseline view without triggering a full rebuild.</summary>
    private bool _baselineShed;

    public SolutionViews(RepoRoot root, ChangeTracker tracker, ProjectLoader projects, Action<string>? log = null)
    {
        _root = root;
        _tracker = tracker;
        _projects = projects;
        _analyzers = new AnalyzerShadow(root);
        _summaries = new SummaryCache(root, log ?? (_ => { }), projects.AssetsHashFallback);
        _patcher = new ViewDocumentPatcher(root, tracker, projects);
        _applicator = new ViewSummaryApplicator(root, tracker, projects, _summaries, _patcher);
    }

    /// <summary>The summaries the views supply references from, in the shared directory.</summary>
    public SummaryCache Summaries => _summaries;

    /// <summary>
    ///     Whether the views supply references to projects outside the change cone from summaries, which is how a check
    ///     binds. It is set once, before the first check, only by a caller that compares the two answers against each
    ///     other (the differential tests in Fuse.Tests); a check with it false binds from source everywhere, and no
    ///     surface reads it, so it is not a setting.
    /// </summary>
    public bool UseSummaries
    {
        get => _applicator.UseSummaries;
        set => _applicator.UseSummaries = value;
    }

    /// <summary>The loaded projects as they are on disk, with references to projects outside the change cone supplied by their summaries when one is kept.</summary>
    public Solution Current => _applicator.SummaryCurrent ?? Sources.Current;

    /// <summary>The loaded projects with every changed file at its HEAD content, with references to projects outside the change cone supplied by their summaries when one is kept.</summary>
    public Solution Baseline => _applicator.SummaryBaseline ?? Sources.Baseline;

    /// <summary>Increments whenever the baseline's content changes (HEAD moved, projects reloaded), which invalidates everything cached against it.</summary>
    public int BaselineGeneration { get; private set; }

    /// <summary>
    ///     True when both views were derived from the loader's solution as it is now, so they hold every project the
    ///     loader has open. A project a background load opened, or one a load opened before its request was cancelled,
    ///     is in neither view until the next <see cref="RebuildAsync"/>.
    /// </summary>
    /// <remarks>
    ///     The loader's solution is an immutable snapshot that the loader replaces when it opens a project, and Fuse never
    ///     applies changes to it, so comparing the instance is enough. A background load publishes a project in
    ///     <see cref="ProjectLoader.IsLoaded"/> after the loader's solution holds it, so a request that finds the project
    ///     loaded also finds this false until it rebuilds.
    /// </remarks>
    public bool HoldEveryOpenProject => _projects.Solution is not { } loaded || ReferenceEquals(loaded, _derivedFrom);

    /// <summary>True when <see cref="ClearHeadCompilations"/> has shed the baseline view and no request has materialized it again yet.</summary>
    public bool IsBaselineShed => _baselineShed;

    /// <summary>Empties both views, after the loader closed every project, and increments <see cref="BaselineGeneration"/>.</summary>
    public void Clear()
    {
        var empty = new AdhocWorkspace().CurrentSolution;
        _sources = new SourcePair(empty, empty);
        _applicator.Reset(empty);
        _derivedFrom = null;
        _shedProjects.Clear();
        _baselineShed = false;
        _patcher.ClearDocumentState();
        _sessionViews.Clear();
        _baselineHead = _tracker.Head;
        BaselineGeneration++;
    }

    /// <summary>How many session views are kept now, so a caller can see that a single-agent sync maintains none.</summary>
    public int SessionViewCount => _sessionViews.Count;

    /// <summary>
    ///     Sheds the session views: the views with some files at HEAD content that a session's check is answered from.
    ///     A later check of that session derives its view from the current view again, as a check did before its view
    ///     was kept, and answers the same.
    /// </summary>
    public void ClearSessionViews()
    {
        _sessionViews.Clear();
    }

    /// <summary>
    ///     Sheds the HEAD compilations by dropping the baseline view, so every compilation built over HEAD can go.
    ///     The working tree view and loader derivation stay intact, avoiding an immediate rebuild on the next sync.
    ///     When a subsequent request needs the baseline view, <see cref="MaterializeBaselineShellAsync"/> rebuilds
    ///     only its shell without throwing away working tree compilations.
    /// </summary>
    public void ClearHeadCompilations()
    {
        if (_sources is null)
            throw new InvalidOperationException("The views are not derived yet. Rebuild the views before reading them.");
        var baseline = new AdhocWorkspace().CurrentSolution;
        _sources = new SourcePair(Sources.Current, baseline);
        _applicator.SetBaselineViews(baseline);
        _baselineShed = true;
        _baselineHead = _tracker.Head;
    }

    /// <summary>
    ///     Sheds the idle cone projects: removes projects outside changed owners and their transitive closure from
    ///     both views while keeping loader derivation synchronized so subsequent edits do not trigger an immediate
    ///     rebuild. Returns the project paths shed so the loader can unload them.
    /// </summary>
    public IReadOnlyList<RepoPath> ClearIdleConeProjects()
    {
        if (_sources is not { } sources || _projects.Graph is not { } graph)
            return [];
        var keep = new HashSet<RepoPath>();
        foreach (var path in _tracker.Changed)
        {
            foreach (var owner in graph.OwnersOf(path))
            {
                foreach (var node in graph.ClosureOf(owner))
                    keep.Add(node.Path);
            }
        }

        var current = sources.Current;
        var baseline = sources.Baseline;
        var shed = new List<RepoPath>();
        foreach (var project in sources.Current.Projects.ToList())
        {
            if (project.FilePath is not { } file || graph.Find(_root.PathOf(file)) is not { } node || keep.Contains(node.Path))
                continue;
            current = current.RemoveProject(project.Id);
            if (baseline.GetProject(project.Id) is not null)
                baseline = baseline.RemoveProject(project.Id);
            _shedProjects.Add(node.Path);
            shed.Add(node.Path);
        }

        if (shed.Count == 0)
            return shed;
        _sources = new SourcePair(current, baseline);
        if (_baselineShed)
            _applicator.SetBaselineViews(baseline);
        ClearSessionViews();
        _applicator.RefreshSummaryViewsFromCache(current, baseline, _baselineShed);
        return shed;
    }

    /// <summary>Marks <paramref name="projectPaths"/> as needed again so the next load or rebuild keeps them.</summary>
    /// <returns>True when any requested project had been shed and is missing from <see cref="Current"/>.</returns>
    public bool Unshed(IEnumerable<RepoPath> projectPaths)
    {
        var needsRebuild = false;
        foreach (var path in projectPaths)
        {
            if (_shedProjects.Remove(path)
                && _projects.Graph?.Find(path) is { } node
                && _sources?.Current is { } current
                && !RepoWorkspace.ProjectsFor(current, node).Any())
                needsRebuild = true;
        }

        return needsRebuild;
    }

    /// <summary>Aligns the loader reference with <see cref="ProjectLoader.Solution"/> after unloading shed projects.</summary>
    public void SyncDerivedFromLoader() => _derivedFrom = _projects.Solution;

    /// <summary>Drops session views and any cached baseline Roslyn compilations while keeping active project shells alive.</summary>
    public void ClearTransientCaches(bool clearSessionViews = true, bool dropCompiledBaseline = true)
    {
        if (clearSessionViews)
            _sessionViews.Clear();
        if (_sources is { } sources && dropCompiledBaseline && !_baselineShed && sources.Baseline is { ProjectIds.Count: > 0 } baseline)
        {
            var fresh = ViewDocumentPatcher.CloneUncompiled(baseline);
            _sources = new SourcePair(sources.Current, fresh);
            _applicator.RefreshSummaryViewsFromCache(sources.Current, fresh, _baselineShed);
        }
    }

    /// <summary>
    ///     Emits the summary of <paramref name="node"/> inside a transient <see cref="AdhocWorkspace"/> and disposes
    ///     that workspace right after emitting so no baseline Roslyn compilation stays pinned in memory.
    /// </summary>
    /// <param name="node">The project whose summary the warm emits, in each target framework it is loaded for.</param>
    /// <param name="cancellationToken">Cancels building the compilation and emitting it.</param>
    /// <returns>True when a summary became kept for a target framework of the project in this call.</returns>
    public async Task<bool> EmitSummaryAsync(ProjectNode node, CancellationToken cancellationToken) =>
        await _applicator.EmitSummaryAsync(node, _sources?.Current, _sources?.Baseline, _baselineShed, cancellationToken).ConfigureAwait(false);

    /// <summary>
    ///     Materializes the baseline view shell from <see cref="Current"/> with HEAD contents applied for changed
    ///     files, without rebuilding <see cref="Current"/> or re-loading shed projects.
    /// </summary>
    public async Task MaterializeBaselineShellAsync(CancellationToken cancellationToken)
    {
        if (!_baselineShed || _sources is not { } sources)
            return;
        var baseline = await _patcher.CloneSolutionAsync(sources.Current, applyHeadTexts: true, cancellationToken).ConfigureAwait(false);
        _sources = new SourcePair(sources.Current, baseline);
        _baselineShed = false;
        _baselineHead = _tracker.Head;
        await _applicator.RefreshSummaryViewsAsync(sources.Current, baseline, _baselineShed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Builds a transient HEAD compilation view for <paramref name="projects"/> and their transitive dependencies
    ///     directly from git HEAD blobs (<see cref="ChangeTracker.ReadHead"/>) so a BaselineStore miss can compile on
    ///     demand and release 100% of baseline Roslyn compilations on disposal.
    /// </summary>
    public async Task<TransientBaseline> BuildTransientBaselineAsync(IReadOnlyCollection<ProjectNode> projects, CancellationToken cancellationToken) =>
        await _patcher.BuildTransientBaselineAsync(_sources?.Current, projects, cancellationToken).ConfigureAwait(false);

    /// <summary>
    ///     Remembers paths whose content may differ from what the loader read, so every later <see cref="RebuildAsync"/>
    ///     applies them again. A path stays remembered after it matches HEAD again, because the loader may still hold its
    ///     older content.
    /// </summary>
    public void Touch(IEnumerable<RepoPath> paths) => _patcher.Touch(paths);

    /// <summary>
    ///     How many touched paths are churn: touched once but no longer changed from HEAD. Past
    ///     <see cref="MaxTouchedPaths"/> of them, the next sync re-evaluates the projects and resets the touched set.
    /// </summary>
    public int ChurnCount => _patcher.ChurnCount;

    /// <summary>Empties the touched set. Called only when the projects are evaluated again, which reopens them from disk.</summary>
    public void ResetTouched() => _patcher.ResetTouched();

    /// <summary>Derives both views from the loader's solution again, re-applying every touched and every changed file.</summary>
    /// <remarks>
    ///     At the same HEAD, <see cref="BaselineGeneration"/> stays the same: the baseline always holds HEAD content for
    ///     the loaded projects, and a load only adds projects, so diagnostics cached against it stay valid. A rebuild at a
    ///     HEAD other than the one the baseline was read at increments it, so no diagnostic cached for the earlier HEAD is
    ///     taken for the new one. A moved HEAD normally clears both views first, when the projects are evaluated again.
    /// </remarks>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var loaded = _projects.Solution;
        if (loaded is null)
            return;
        // The repository's own analyzers load from a copy, so a real build can still overwrite them.
        var current = _analyzers.Apply(loaded);
        if (_shedProjects.Count > 0 && _projects.Graph is { } graph)
        {
            foreach (var project in current.Projects.ToList())
            {
                if (project.FilePath is { } file && graph.Find(_root.PathOf(file)) is { } node && _shedProjects.Contains(node.Path))
                    current = current.RemoveProject(project.Id);
            }
        }

        var baseline = current;
        var head = _tracker.Head;
        foreach (var path in _patcher.TouchedAndChangedPaths())
        {
            current = await _patcher.WithDiskContentAsync(current, path, cancellationToken).ConfigureAwait(false);
            baseline = await _patcher.WithHeadContentAsync(baseline, path, cancellationToken).ConfigureAwait(false);
        }

        _sources = new SourcePair(current, baseline);
        _baselineShed = false;
        _derivedFrom = loaded;
        if (!string.Equals(head, _baselineHead, StringComparison.Ordinal))
        {
            _baselineHead = head;
            BaselineGeneration++;
            _applicator.ClearEntries();
        }

        // The loader read the generated files when it opened their projects. A file a build wrote again since then holds
        // the content applied after that build, which the views derived again from the loader no longer have; the others
        // are read once, when they first appear, and only compared by length and write time after that.
        _patcher.RefreshGeneratedFiles(current);
        (current, baseline) = await _patcher.ApplyRewrittenAsync(current, baseline, cancellationToken).ConfigureAwait(false);
        _sources = new SourcePair(current, baseline);
        await RefreshGeneratedAsync(cancellationToken).ConfigureAwait(false);
        await _applicator.RefreshSummaryViewsAsync(current, baseline, _baselineShed, cancellationToken).ConfigureAwait(false);
        await RederiveSessionViewsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Applies the content of each generated file that a build wrote since its content was last applied to both views,
    ///     and increments <see cref="BaselineGeneration"/> when the baseline changed. A generated file is not in git, so
    ///     HEAD has no content of its own for it; both views hold the file as the last build wrote it.
    /// </summary>
    /// <remarks>
    ///     The watcher skips <c>bin</c> and <c>obj</c> folders, so these files are compared by length and write time on
    ///     every request instead. A file that is missing or being written stays as it was, and the next request reads it.
    /// </remarks>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task RefreshGeneratedAsync(CancellationToken cancellationToken)
    {
        if (!_patcher.HasGeneratedFiles)
            return;
        var sources = Sources;
        var before = Current;
        // The summary views follow the generated files only when they are in step with the source views; in a
        // rebuild they are derived from the updated sources after this, so applying the files here would be
        // work on views that are about to go.
        var summaryInStep = _applicator.IsSummaryInStep(sources.Current, sources.Baseline, _baselineShed);
        var (current, baseline, applied, baselineChanged) = await _patcher.ApplyGeneratedAsync(sources.Current, sources.Baseline, _baselineShed, cancellationToken).ConfigureAwait(false);
        _sources = new SourcePair(current, baseline);
        if (baselineChanged)
            BaselineGeneration++;

        await _applicator.ApplyGeneratedToSummaryViewsAsync(summaryInStep, current, baseline, _baselineShed, applied, cancellationToken).ConfigureAwait(false);

        foreach (var entry in _sessionViews.From(before))
        {
            var view = entry.View;
            foreach (var (path, text) in applied)
            {
                if (!entry.HeadPaths.Contains(path))
                    view = await _patcher.WithContentAsync(view, path, text, cancellationToken).ConfigureAwait(false);
            }

            _sessionViews.Replace(entry, Current, ViewSummaryApplicator.WithReferencesFrom(view, Current));
        }
    }

    /// <summary>
    ///     Applies each path's disk content to <see cref="Current"/> and its HEAD content to <see cref="Baseline"/>, and
    ///     increments <see cref="BaselineGeneration"/> when the baseline changed. Nothing is applied while no project is
    ///     open, because the loader reads every file from disk when it opens its project.
    /// </summary>
    /// <param name="paths">The files to apply; each is also touched.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task PatchAsync(IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0 || _projects.Solution is null)
            return;
        var sources = Sources;
        var before = Current;
        var summaryInStep = _applicator.IsSummaryInStep(sources.Current, sources.Baseline, _baselineShed);
        var appliedBefore = _applicator.AppliedSnapshot();
        var (current, baseline, baselineChanged) = await _patcher.ApplyPatchAsync(sources.Current, sources.Baseline, _baselineShed, paths, cancellationToken).ConfigureAwait(false);
        _sources = new SourcePair(current, baseline);
        if (baselineChanged)
            BaselineGeneration++;

        // The summary views take the patched files with no reference flip: a project supplied now stays
        // supplied, unless the cone has taken it, which the derived views below settle from source.
        var cone = _applicator.ConePaths();
        var retained = _applicator.RetainedSummaries(cone, current, baseline, _baselineShed);
        await _applicator.PatchSummaryViewsAsync(summaryInStep, current, baseline, _baselineShed, paths, retained, appliedBefore, cone, cancellationToken).ConfigureAwait(false);

        // The writer keeps each session view in step with the tree it just patched: a patched file takes its disk
        // content in a view, unless that view holds the file at HEAD, where it stays. Document ids are preserved, so
        // the errors a check kept for a file still hit in the view it is decided in.
        foreach (var entry in _sessionViews.From(before))
        {
            var view = entry.View;
            foreach (var path in paths)
            {
                if (!entry.HeadPaths.Contains(path))
                    view = await _patcher.WithDiskContentAsync(view, path, cancellationToken).ConfigureAwait(false);
            }

            _sessionViews.Replace(entry, Current, ViewSummaryApplicator.WithReferencesFrom(view, Current));
        }
    }

    /// <summary>
    ///     <see cref="Current"/> with each of <paramref name="paths"/> at its HEAD content, and a file HEAD does not have
    ///     left out. Neither view changes; the result shares every other document with <see cref="Current"/>, so only the
    ///     projects that compile those files bind again. The result is kept as a session view for the same view and
    ///     paths, up to <see cref="MaxSessionViews"/> kept with the one used least recently going first, and shed by
    ///     <see cref="ClearSessionViews"/>. A patch keeps a kept view in step with the tree, so a view read after a
    ///     sync is the one that sync left; a session whose view went derives it from the current view again and is
    ///     answered the same, only slower.
    /// </summary>
    /// <param name="view">The current view to derive from, as a pinned generation holds it.</param>
    /// <param name="paths">The files to put back at HEAD content.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public async Task<Solution> CurrentWithHeadContentAsync(Solution view, IEnumerable<RepoPath> paths, CancellationToken cancellationToken)
    {
        var listed = (paths as IReadOnlyCollection<RepoPath> ?? [.. paths]).Distinct().ToList();
        var key = string.Join("\n", listed.Select(p => p.Relative).Order(StringComparer.Ordinal));
        if (_sessionViews.TryTake(view, key, out var kept))
            return kept;

        var solution = view;
        foreach (var path in listed)
            solution = await _patcher.WithHeadContentAsync(solution, path, cancellationToken).ConfigureAwait(false);
        _sessionViews.Add(view, key, listed, solution);

        return solution;
    }

    /// <summary>
    ///     Derives every kept session view from the current view again, after a rebuild derived that view from the
    ///     loader. The projects a rebuild opens have compilations of their own to build, so a view derived here binds
    ///     them as a first check after the rebuild does; its files at HEAD are the same.
    /// </summary>
    private async Task RederiveSessionViewsAsync(CancellationToken cancellationToken)
    {
        var entries = _sessionViews.All();
        // No view kept: a rebuild maintains none, as a sync does.
        if (entries.Count == 0)
            return;
        foreach (var entry in entries)
        {
            var view = Current;
            foreach (var path in entry.HeadPaths)
                view = await _patcher.WithHeadContentAsync(view, path, cancellationToken).ConfigureAwait(false);
            _sessionViews.Replace(entry, Current, view);
        }
    }

    public async Task<Solution> CurrentWithHeadContentAsync(IEnumerable<RepoPath> paths, CancellationToken cancellationToken) =>
        await CurrentWithHeadContentAsync(Current, paths, cancellationToken).ConfigureAwait(false);

    /// <summary>The files of the documents and additional documents <see cref="Current"/> holds under <paramref name="directory"/>.</summary>
    public IEnumerable<RepoPath> FilesUnder(RepoPath directory) =>
        _patcher.FilesUnder(Current, directory);

    /// <summary>Returns the file's HEAD content as source text, or null when the file is new.</summary>
    public SourceText? HeadText(RepoPath path) => _patcher.HeadText(path);
    /// <summary>
    ///     Reads file bytes as source text: UTF-8 unless a byte order mark names another encoding, with a SHA-256
    ///     checksum. A file that looks binary is read as text rather than refused, so one odd file cannot fail a request.
    /// </summary>
    public static SourceText Decode(byte[] bytes) => ViewDocumentPatcher.Decode(bytes);

    /// <summary>Makes every document for <paramref name="path"/> hold <paramref name="text"/>, adding or removing documents as needed.</summary>
    /// <remarks>A document that already holds the same content is left alone, so its compilation stays cached.</remarks>
    internal async Task<Solution> WithContentAsync(Solution solution, RepoPath path, SourceText? text, CancellationToken cancellationToken) =>
        await _patcher.WithContentAsync(solution, path, text, cancellationToken).ConfigureAwait(false);
}
