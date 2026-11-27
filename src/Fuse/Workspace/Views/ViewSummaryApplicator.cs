using Fuse.Graph;
using Fuse.Paths;
using Fuse.Repo.Tracking;
using Fuse.Workspace.Blueprints;
using Fuse.Workspace.Generations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Workspace.Views;

/// <summary>
///     The change-cone summaries of the views: which projects outside the change cone are supplied by their
///     project summary, the kept summary views derived from the source views, and the background emission of
///     summaries. <see cref="SolutionViews"/> owns the source views and calls here to derive the summary views
///     from them.
/// </summary>
internal sealed class ViewSummaryApplicator
{
    private readonly RepoRoot _root;
    private readonly ChangeTracker _tracker;
    private readonly ProjectLoader _projects;
    private readonly SummaryCache _summaries;
    private readonly ViewDocumentPatcher _patcher;

    /// <summary>The summary each baseline project was last looked up for, for the version of that project it was looked up for, so a view derived again does not key it again; a null reference says none was kept for that version.</summary>
    private readonly Dictionary<ProjectId, (VersionStamp Version, MetadataReference? Reference)> _summaryReferences = [];

    /// <summary>
    ///     The projects the kept summary views supply by summary now. A project joins only when the views are
    ///     derived from the loader again, and leaves at the next sync that puts it in the change cone, so a sync
    ///     never flips a project from source to summary and forces its dependents to bind again.
    /// </summary>
    private readonly HashSet<ProjectId> _appliedSummaries = [];

    /// <summary>Guards <see cref="_summaryReferences"/> and <see cref="_appliedSummaries"/>.</summary>
    private readonly Lock _summaryLock = new();

    /// <summary>The source view the kept summary view of the current view was derived from, so a round that changed neither view reuses it.</summary>
    private Solution? _summarySourceCurrent;

    /// <summary>The source view the kept summary view of the baseline view was derived from.</summary>
    private Solution? _summarySourceBaseline;

    /// <summary>The current view with summaries outside the change cone, or null when it is the source view itself.</summary>
    private Solution? _summaryCurrent;

    /// <summary>The baseline view with summaries outside the change cone, or null when it is the source view itself.</summary>
    private Solution? _summaryBaseline;

    /// <summary>Whether the kept summary views were derived with summaries, as <see cref="UseSummaries"/> was when they were derived.</summary>
    private bool _summaryApplied;

    private bool _useSummaries = true;

    public ViewSummaryApplicator(RepoRoot root, ChangeTracker tracker, ProjectLoader projects, SummaryCache summaries, ViewDocumentPatcher patcher)
    {
        _root = root;
        _tracker = tracker;
        _projects = projects;
        _summaries = summaries;
        _patcher = patcher;
    }

    /// <summary>
    ///     Whether the views supply references to projects outside the change cone from summaries, which is how a check
    ///     binds. It is set once, before the first check, only by a caller that compares the two answers against each
    ///     other (the differential tests in Fuse.Tests); a check with it false binds from source everywhere, and no
    ///     surface reads it, so it is not a setting.
    /// </summary>
    public bool UseSummaries
    {
        get => _useSummaries;
        set => _useSummaries = value;
    }

    /// <summary>The current view with summaries outside the change cone, or null when it is the source view itself.</summary>
    internal Solution? SummaryCurrent => _summaryCurrent;

    /// <summary>The baseline view with summaries outside the change cone, or null when it is the source view itself.</summary>
    internal Solution? SummaryBaseline => _summaryBaseline;

    /// <summary>The project files of the change cone: the owners of the changed files, and every dependent of an owner.</summary>
    internal HashSet<RepoPath> ConePaths() => _projects.Graph is { } graph ? ConePaths(graph) : [];

    /// <summary>The cone over <paramref name="graph"/>: the owners of the changed files, and every dependent of an owner.</summary>
    internal HashSet<RepoPath> ConePaths(RepoGraph graph)
    {
        var cone = new HashSet<RepoPath>();
        var queue = new Queue<ProjectNode>();
        foreach (var path in _tracker.Changed)
        {
            foreach (var owner in graph.OwnersOf(path))
            {
                if (cone.Add(owner.Path))
                    queue.Enqueue(owner);
            }
        }

        while (queue.Count > 0)
        {
            foreach (var dependent in graph.DependentsOf(queue.Dequeue()))
            {
                if (cone.Add(dependent.Path))
                    queue.Enqueue(dependent);
            }
        }

        return cone;
    }

    /// <summary>
    ///     <paramref name="source"/> with each reference to a project outside <paramref name="cone"/> supplied by the
    ///     summary <paramref name="summaryFor"/> gives for that project at HEAD, when it gives one. A reference with
    ///     an alias or an embedded interop type, a reference to a project the graph or the baseline does not hold,
    ///     and a project with no summary kept all stay as they are: a doubt falls back to source.
    /// </summary>
    private Solution WithSummaries(Solution source, Solution baseline, HashSet<RepoPath> cone, Func<Project, MetadataReference?> summaryFor)
    {
        if (_projects.Graph is not { } graph)
            return source;
        var result = source;
        foreach (var project in source.Projects)
        {
            List<ProjectReference>? kept = null;
            List<MetadataReference>? added = null;
            foreach (var reference in project.ProjectReferences)
            {
                if (reference.ProjectId == project.Id || !reference.Aliases.IsEmpty || reference.EmbedInteropTypes)
                    continue;
                if (source.GetProject(reference.ProjectId)?.FilePath is not { } referencedFile)
                    continue;
                if (graph.Find(_root.PathOf(referencedFile)) is not { } node || cone.Contains(node.Path))
                    continue;
                if (baseline.GetProject(reference.ProjectId) is not { } baselineProject)
                    continue;
                if (summaryFor(baselineProject) is not { } summary)
                    continue;
                kept ??= [.. project.ProjectReferences];
                added ??= [];
                kept.Remove(reference);
                added.Add(summary);
            }

            if (kept is not null && added is { Count: > 0 })
            {
                result = result.WithProjectReferences(project.Id, kept).WithProjectMetadataReferences(project.Id, project.MetadataReferences.Concat(added));
                // A project supplied by a summary is bound from metadata, where a member the surface hides
                // counts only when every member is imported, as the compiler imports them: against source
                // such a member is inaccessible (CS0122), and the summary view must say the same, so a
                // project whose references a summary supplies imports all members.
                if (project.CompilationOptions is { } compilationOptions)
                    result = result.WithProjectCompilationOptions(project.Id, compilationOptions.WithMetadataImportOptions(MetadataImportOptions.All));
            }
        }

        return result;
    }

    /// <summary>The summary kept for <paramref name="baselineProject"/> at the version it is now, or null when none was kept for it.</summary>
    private MetadataReference? CachedSummary(Project baselineProject)
    {
        lock (_summaryLock)
            return _summaryReferences.TryGetValue(baselineProject.Id, out var entry) && entry.Version == baselineProject.Version ? entry.Reference : null;
    }

    /// <summary>The summary last kept for <paramref name="projectId"/> regardless of project version, used while the baseline shell is shed.</summary>
    private MetadataReference? CachedSummary(ProjectId projectId)
    {
        lock (_summaryLock)
            return _summaryReferences.TryGetValue(projectId, out var entry) ? entry.Reference : null;
    }

    /// <summary>True when a summary was looked up for <paramref name="baselineProject"/> at the version it is now, kept or not, so a rebuild does not key it again.</summary>
    private bool HasSummaryEntry(Project baselineProject)
    {
        lock (_summaryLock)
            return _summaryReferences.TryGetValue(baselineProject.Id, out var entry) && entry.Version == baselineProject.Version;
    }

    /// <summary>Remembers the summary looked up for <paramref name="baselineProject"/> at its version, or that none is kept for it.</summary>
    private void StoreSummaryEntry(Project baselineProject, MetadataReference? reference)
    {
        lock (_summaryLock)
            _summaryReferences[baselineProject.Id] = (baselineProject.Version, reference);
    }

    /// <summary>
    ///     Derives both summary views from the source views, supplied by the projects in <paramref name="supplied"/>
    ///     that have a summary kept. It emits nothing and keys nothing.
    /// </summary>
    internal void DeriveSummaryViews(Solution sourceCurrent, Solution sourceBaseline, bool baselineShed, HashSet<ProjectId> supplied, HashSet<RepoPath> cone)
    {
        _summaryApplied = UseSummaries;
        lock (_summaryLock)
        {
            _appliedSummaries.Clear();
            if (UseSummaries)
                _appliedSummaries.UnionWith(supplied);
        }

        if (!UseSummaries)
        {
            _summaryCurrent = sourceCurrent;
            _summaryBaseline = sourceBaseline;
        }
        else if (baselineShed)
        {
            _summaryCurrent = WithSummaries(sourceCurrent, sourceCurrent, cone, project => supplied.Contains(project.Id) ? CachedSummary(project.Id) : null);
            _summaryBaseline = sourceBaseline;
        }
        else
        {
            _summaryCurrent = WithSummaries(sourceCurrent, sourceBaseline, cone, baselineProject => supplied.Contains(baselineProject.Id) ? CachedSummary(baselineProject) : null);
            _summaryBaseline = WithSummaries(sourceBaseline, sourceBaseline, cone, baselineProject => supplied.Contains(baselineProject.Id) ? CachedSummary(baselineProject) : null);
        }

        _summarySourceCurrent = sourceCurrent;
        _summarySourceBaseline = sourceBaseline;
    }

    /// <summary>
    ///     The projects supplied by a summary that stay supplied on a sync: the ones supplied now that are still
    ///     outside the cone and still have a summary kept. A project the cone has taken binds from source, and no
    ///     project joins on a sync, so a summary the background emitted flips nothing until the views are derived
    ///     from the loader again.
    /// </summary>
    internal HashSet<ProjectId> RetainedSummaries(HashSet<RepoPath> cone, Solution? sourceCurrent, Solution? sourceBaseline, bool baselineShed)
    {
        HashSet<ProjectId> applied;
        lock (_summaryLock)
            applied = [.. _appliedSummaries];
        var retained = new HashSet<ProjectId>();
        var lookup = baselineShed ? sourceCurrent : sourceBaseline;
        if (!UseSummaries || lookup is null || _projects.Graph is not { } graph)
            return retained;
        foreach (var id in applied)
        {
            if (lookup.GetProject(id) is not { } project || project.FilePath is not { } file)
                continue;
            if (graph.Find(_root.PathOf(file)) is not { } node || cone.Contains(node.Path))
                continue;
            var summary = baselineShed ? CachedSummary(id) : CachedSummary(project);
            if (summary is not null)
                retained.Add(id);
        }

        return retained;
    }

    /// <summary>
    ///     Derives both summary views from the source views again for a sync, keeping only the summaries supplied
    ///     now, and emits nothing. Used where the source views change without being derived from the loader again.
    /// </summary>
    internal void RefreshSummaryViewsFromCache(Solution? sourceCurrent, Solution? sourceBaseline, bool baselineShed)
    {
        if (sourceCurrent is null || sourceBaseline is null)
            return;
        var cone = ConePaths();
        DeriveSummaryViews(sourceCurrent, sourceBaseline, baselineShed, RetainedSummaries(cone, sourceCurrent, sourceBaseline, baselineShed), cone);
    }

    /// <summary>
    ///     Derives both summary views from the source views again when they are derived from the loader, supplied
    ///     by every summary already kept for a project outside the cone. A kept summary is read from the shared
    ///     cache, never emitted: emitting is the background warm's work, off the request path and never under the
    ///     writer lock. This runs only on that loader derivation, never on a sync, so the key it reads a kept
    ///     summary under is paid when projects load, reload or HEAD moves and at no other time. A round that
    ///     changed neither source view and kept the same summaries supplied reuses the views it derived last; a
    ///     summary kept since then, by the background warm, is supplied here even when the source views are the
    ///     same instances.
    /// </summary>
    internal async Task RefreshSummaryViewsAsync(Solution? sourceCurrent, Solution? sourceBaseline, bool baselineShed, CancellationToken cancellationToken)
    {
        if (sourceCurrent is null || sourceBaseline is null || baselineShed)
            return;
        var cone = ConePaths();
        var supplied = new HashSet<ProjectId>();
        if (UseSummaries && _projects.Graph is { } graph)
        {
            var needed = new HashSet<ProjectId>();
            foreach (var project in sourceCurrent.Projects.Concat(sourceBaseline.Projects))
            {
                foreach (var reference in project.ProjectReferences)
                {
                    if (sourceBaseline.GetProject(reference.ProjectId) is not { } baselineProject || baselineProject.FilePath is not { } referencedFile)
                        continue;
                    if (graph.Find(_root.PathOf(referencedFile)) is not { } node || cone.Contains(node.Path))
                        continue;
                    if (!needed.Add(reference.ProjectId))
                        continue;
                    if (!HasSummaryEntry(baselineProject)
                        && await _summaries.KeyForAsync(_tracker.Head, baselineProject, graph, cancellationToken).ConfigureAwait(false) is { } key)
                        StoreSummaryEntry(baselineProject, _summaries.TryGet(key, baselineProject));
                    if (CachedSummary(baselineProject) is not null)
                        supplied.Add(baselineProject.Id);
                }
            }
        }

        HashSet<ProjectId> applied;
        lock (_summaryLock)
            applied = [.. _appliedSummaries];
        if (ReferenceEquals(sourceCurrent, _summarySourceCurrent) && ReferenceEquals(sourceBaseline, _summarySourceBaseline) && _summaryCurrent is not null && _summaryApplied == UseSummaries && supplied.SetEquals(applied))
            return;

        DeriveSummaryViews(sourceCurrent, sourceBaseline, baselineShed, supplied, cone);
    }

    /// <summary>
    ///     Emits the summary of <paramref name="node"/> inside a transient <see cref="AdhocWorkspace"/> and disposes
    ///     that workspace right after emitting so no baseline Roslyn compilation stays pinned in memory.
    /// </summary>
    /// <param name="node">The project whose summary the warm emits, in each target framework it is loaded for.</param>
    /// <param name="sourceCurrent">The working tree before summaries.</param>
    /// <param name="sourceBaseline">The baseline before summaries.</param>
    /// <param name="baselineShed">True when the baseline view has been shed without a rebuild.</param>
    /// <param name="cancellationToken">Cancels building the compilation and emitting it.</param>
    /// <returns>True when a summary became kept for a target framework of the project in this call.</returns>
    internal async Task<bool> EmitSummaryAsync(ProjectNode node, Solution? sourceCurrent, Solution? sourceBaseline, bool baselineShed, CancellationToken cancellationToken)
    {
        if (!UseSummaries || _projects.Graph is not { } graph)
            return false;
        if (ConePaths(graph).Contains(node.Path))
            return false;
        var sourceView = !baselineShed && sourceBaseline is { ProjectIds.Count: > 0 } ? sourceBaseline : sourceCurrent;
        if (sourceView is null)
            return false;
        var head = _tracker.Head;
        var emitted = false;
        foreach (var sourceProject in RepoWorkspace.ProjectsFor(sourceView, node).ToList())
        {
            if (!baselineShed && CachedSummary(sourceProject) is not null)
                continue;
            MetadataReference? reference = null;
            using var transient = await _patcher.CloneClosureIntoTransientAsync(sourceView, [sourceProject.Id], applyHeadTexts: baselineShed, cancellationToken).ConfigureAwait(false);
            if (transient.Solution.GetProject(sourceProject.Id) is { } transientProject
                && await _summaries.KeyForAsync(head, transientProject, graph, cancellationToken).ConfigureAwait(false) is { } key)
            {
                reference = await _summaries.GetOrEmitAsync(transientProject, key, cancellationToken).ConfigureAwait(false);
            }

            StoreSummaryEntry(sourceProject, reference);
            emitted |= reference is not null;
        }

        return emitted;
    }

    /// <summary>
    ///     <paramref name="view"/> with each project's references taken from the same project of
    ///     <paramref name="source"/>: the summaries a project's references are supplied by follow the change cone as
    ///     the tree view has it, while the view's own documents, the files it holds at HEAD among them, stay.
    /// </summary>
    internal static Solution WithReferencesFrom(Solution view, Solution source)
    {
        var result = view;
        foreach (var project in view.Projects)
        {
            if (source.GetProject(project.Id) is not { } from)
                continue;
            if (!project.ProjectReferences.SequenceEqual(from.ProjectReferences))
                result = result.WithProjectReferences(project.Id, from.ProjectReferences);
            var supplied = project.MetadataReferences;
            var wanted = from.MetadataReferences;
            var differs = supplied.Count != wanted.Count;
            for (var i = 0; !differs && i < supplied.Count; i++)
                differs = !ReferenceEquals(supplied[i], wanted[i]);
            if (differs)
                result = result.WithProjectMetadataReferences(project.Id, wanted);
        }

        return result;
    }

    /// <summary>True when the kept summary views were derived from <paramref name="sourceCurrent"/> and <paramref name="sourceBaseline"/>.</summary>
    internal bool IsSummaryInStep(Solution sourceCurrent, Solution sourceBaseline, bool baselineShed) =>
        ReferenceEquals(_summarySourceCurrent, sourceCurrent)
            && (baselineShed || ReferenceEquals(_summarySourceBaseline, sourceBaseline));

    /// <summary>The projects the kept summary views supply by summary now, as a copy.</summary>
    internal HashSet<ProjectId> AppliedSnapshot()
    {
        lock (_summaryLock)
            return [.. _appliedSummaries];
    }

    /// <summary>Replaces the kept summary views with <paramref name="summaryCurrent"/> and <paramref name="summaryBaseline"/> derived from the source views.</summary>
    internal void SetSummaryViews(Solution summaryCurrent, Solution summaryBaseline, Solution sourceCurrent, Solution sourceBaseline)
    {
        _summaryCurrent = summaryCurrent;
        _summaryBaseline = summaryBaseline;
        _summarySourceCurrent = sourceCurrent;
        _summarySourceBaseline = sourceBaseline;
    }

    /// <summary>Resets the kept summary views to <paramref name="empty"/> after both views were emptied.</summary>
    internal void Reset(Solution empty)
    {
        _summaryCurrent = empty;
        _summaryBaseline = empty;
        _summarySourceCurrent = empty;
        _summarySourceBaseline = empty;
        lock (_summaryLock)
        {
            _summaryReferences.Clear();
            _appliedSummaries.Clear();
            _summaries.ReleaseReferences();
        }
    }

    /// <summary>Drops the kept summary entries after the baseline was shed.</summary>
    internal void SetBaselineViews(Solution baseline)
    {
        _summaryBaseline = baseline;
        _summarySourceBaseline = baseline;
    }

    /// <summary>Clears the kept summary entries, after a rebuild at a HEAD other than the one the baseline was read at.</summary>
    internal void ClearEntries()
    {
        lock (_summaryLock)
        {
            _summaryReferences.Clear();
            _appliedSummaries.Clear();
        }
    }

    /// <summary>
    ///     Takes patched files into the kept summary views with no reference flip when the supplied set is as it
    ///     was, and derives the summary views from source otherwise. The summary views take the files themselves,
    ///     so no project's references are rebuilt and no dependent binds again for the patch.
    /// </summary>
    internal async Task PatchSummaryViewsAsync(bool summaryInStep, Solution sourceCurrent, Solution sourceBaseline, bool baselineShed, IReadOnlyCollection<RepoPath> paths, HashSet<ProjectId> retained, HashSet<ProjectId> appliedBefore, HashSet<RepoPath> cone, CancellationToken cancellationToken)
    {
        if (summaryInStep && retained.SetEquals(appliedBefore) && _summaryCurrent is not null && _summaryBaseline is not null)
        {
            var summaryCurrent = _summaryCurrent;
            var summaryBaseline = _summaryBaseline;
            foreach (var path in paths)
            {
                summaryCurrent = await _patcher.WithDiskContentAsync(summaryCurrent, path, cancellationToken).ConfigureAwait(false);
                if (!baselineShed)
                    summaryBaseline = await _patcher.WithHeadContentAsync(summaryBaseline, path, cancellationToken).ConfigureAwait(false);
            }

            SetSummaryViews(summaryCurrent, summaryBaseline, sourceCurrent, sourceBaseline);
        }
        else
        {
            DeriveSummaryViews(sourceCurrent, sourceBaseline, baselineShed, retained, cone);
        }
    }

    /// <summary>Applies generated file contents to the kept summary views when they are in step with the source views.</summary>
    internal async Task ApplyGeneratedToSummaryViewsAsync(bool summaryInStep, Solution sourceCurrent, Solution sourceBaseline, bool baselineShed, List<(RepoPath Path, SourceText Text)> applied, CancellationToken cancellationToken)
    {
        if (summaryInStep && _summaryCurrent is not null && _summaryBaseline is not null)
        {
            var summaryCurrent = _summaryCurrent;
            var summaryBaseline = _summaryBaseline;
            foreach (var (path, text) in applied)
            {
                summaryCurrent = await _patcher.WithContentAsync(summaryCurrent, path, text, cancellationToken).ConfigureAwait(false);
                if (!baselineShed)
                    summaryBaseline = await _patcher.WithContentAsync(summaryBaseline, path, text, cancellationToken).ConfigureAwait(false);
            }

            SetSummaryViews(summaryCurrent, summaryBaseline, sourceCurrent, sourceBaseline);
        }
    }
}
