using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Repo;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Workspace;

/// <summary>
///     Per-worktree overlay mounted under one repository hypervisor (<see cref="RepoWorkspace"/>). Each worktree
///     shares the repository's immutable HEAD base layer (<see cref="BlueprintWorkspace.SharedResources"/>,
///     <see cref="SummaryCache"/>, and stored HEAD baselines) and maintains a compact <c>Dictionary&lt;RepoPath, SourceText?&gt;</c>
///     delta map for files that differ from HEAD plus its own <see cref="SolutionViews"/> and <see cref="Generation"/>
///     pinning.
/// </summary>
internal sealed class WorktreeOverlay : IDisposable
{
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Lock _deltaLock = new();
    private readonly Dictionary<RepoPath, SourceText?> _deltas = [];
    private HashSet<RepoPath> _dirtySet = [];
    private Generation? _generation;
    private long _generationNumber;

    public WorktreeOverlay(
        RepoRoot root,
        Action<string> log,
        BlueprintWorkspace.SharedResources? sharedResources = null)
    {
        Root = root;
        _log = log;
        Tracker = new ChangeTracker(root);
        Projects = new ProjectLoader(root, log, sharedResources, () => Tracker.Head, GetDirtyPaths);
        Views = new SolutionViews(root, Tracker, Projects, log);
        Sync = new WorkspaceSync(Tracker, Projects, Views, log);
    }

    public RepoRoot Root { get; }

    public ChangeTracker Tracker { get; }

    public ProjectLoader Projects { get; }

    public SolutionViews Views { get; }

    public WorkspaceSync Sync { get; }

    /// <summary>Shared immutable metadata, analyzer, and HEAD text resources shared across every overlay in the hypervisor.</summary>
    public BlueprintWorkspace.SharedResources SharedResources => Projects.SharedResources;

    public RepoGraph Graph => Projects.Graph;

    /// <summary>The loaded projects as they are on disk.</summary>
    public Solution Current => Views.Current;

    /// <summary>The loaded projects with every changed file at its HEAD content.</summary>
    public Solution Baseline => Views.Baseline;

    /// <inheritdoc cref="SolutionViews.BaselineGeneration"/>
    public int BaselineGeneration => Views.BaselineGeneration;

    /// <inheritdoc cref="ProjectLoader.ConfigurationGeneration"/>
    public int ConfigurationGeneration => Projects.ConfigurationGeneration;

    /// <summary>The summaries shared across worktrees of the same repository.</summary>
    public SummaryCache Summaries => Views.Summaries;

    /// <summary>How many session views this overlay holds right now.</summary>
    public int SessionViewCount => Views.SessionViewCount;

    /// <summary>True when this worktree loaded projects by mapping onto another worktree's restored blueprint without local restore assets.</summary>
    public bool IsZeroRestore => Projects.IsUsingZeroRestoreBlueprint;

    public void Log(string message) => _log(message);

    /// <inheritdoc cref="SolutionViews.UseSummaries"/>
    public bool UseSummaries
    {
        get => Views.UseSummaries;
        set => Views.UseSummaries = value;
    }

    /// <summary>Snapshot of this overlay's working-tree deltas relative to HEAD (null value indicates a deleted file).</summary>
    public IReadOnlyDictionary<RepoPath, SourceText?> Deltas
    {
        get
        {
            lock (_deltaLock)
                return new Dictionary<RepoPath, SourceText?>(_deltas);
        }
    }

    /// <summary>How many source deltas against HEAD this worktree overlay currently holds.</summary>
    public int DeltaCount
    {
        get
        {
            lock (_deltaLock)
                return _deltas.Count;
        }
    }

    /// <summary>Looks up the overlay delta for <paramref name="path"/> (a null <paramref name="text"/> when present indicates deletion).</summary>
    public bool TryGetDelta(RepoPath path, out SourceText? text)
    {
        lock (_deltaLock)
            return _deltas.TryGetValue(path, out text);
    }

    /// <summary>The newest published generation for this worktree.</summary>
    public Generation PinGeneration() => Volatile.Read(ref _generation) ?? PublishInitial();

    /// <summary>Evaluates the project graph and starts tracking changes for this worktree without compiling.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Tracker.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await Sync.EvaluateAsync(cancellationToken).ConfigureAwait(false);
            RefreshAllDeltas();
            PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Reads working tree changes since the last sync and updates views and deltas.</summary>
    public async Task SyncAsync(IEnumerable<RepoPath> knownPaths, CancellationToken cancellationToken)
    {
        await SyncAndPinAsync(knownPaths, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Syncs the overlay under the writer lock and pins the generation that sync published.</summary>
    public async Task<Generation> SyncAndPinAsync(IEnumerable<RepoPath> knownPaths, CancellationToken cancellationToken, bool confirmWithDisk = false)
    {
        var known = knownPaths as IReadOnlyCollection<RepoPath> ?? [.. knownPaths];
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Sync.SyncAsync(known, cancellationToken, confirmWithDisk).ConfigureAwait(false);
            RefreshAllDeltas();
            return PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Loads <paramref name="projects"/> and their transitive closure into this worktree overlay.</summary>
    public Task EnsureLoadedAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken) =>
        EnsureLoadedAndPinAsync(projects, cancellationToken);

    /// <summary>Loads <paramref name="projects"/> under the writer lock and returns the published generation.</summary>
    public async Task<Generation> EnsureLoadedAndPinAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Sync.EnsureLoadedAsync(projects, cancellationToken).ConfigureAwait(false);
            RefreshAllDeltas();
            return PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Applies disk and HEAD contents for <paramref name="paths"/> to this overlay's views.</summary>
    public Task PatchViewsAsync(IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken) =>
        PatchAndPinAsync(paths, cancellationToken);

    /// <summary>Applies <paramref name="paths"/> under the writer lock, updates the delta map, and pins the published generation.</summary>
    public async Task<Generation> PatchAndPinAsync(IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Sync.PatchViewsAsync(paths, cancellationToken).ConfigureAwait(false);
            RefreshDeltaPaths(paths);
            return PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Rebuilds both views from the loader solution and re-applies all touched and changed paths.</summary>
    public Task RebuildViewsAsync(CancellationToken cancellationToken) =>
        RebuildAndPinAsync(cancellationToken);

    /// <summary>Rebuilds both views under the writer lock and pins the published generation.</summary>
    public async Task<Generation> RebuildAndPinAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Sync.RebuildAsync(cancellationToken).ConfigureAwait(false);
            RefreshAllDeltas();
            return PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Preloads <paramref name="project"/> outside request handling so the next writer round folds it in.</summary>
    public Task PreloadAsync(ProjectNode project, CancellationToken cancellationToken) =>
        Projects.PreloadAsync(project, cancellationToken);

    public bool IsLoaded(ProjectNode node) => Projects.IsLoaded(node);

    public string? LoadFailureOf(ProjectNode node) => Projects.LoadFailureOf(node);

    /// <inheritdoc cref="SolutionViews.EmitSummaryAsync"/>
    public Task<bool> EmitSummaryAsync(ProjectNode node, CancellationToken cancellationToken) =>
        Views.EmitSummaryAsync(node, cancellationToken);

    /// <summary>Sheds session views under the writer lock.</summary>
    public async Task ClearSessionViewsAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Views.ClearSessionViews();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Sheds HEAD compilations under the writer lock without invalidating loader derivation.</summary>
    public async Task ClearHeadCompilationsAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Views.ClearHeadCompilations();
            PublishLocked();
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>
    ///     Sheds idle cone projects from both views and unloads them from the loader while keeping loader derivation
    ///     synchronized so subsequent edits do not trigger an immediate rebuild.
    /// </summary>
    public async Task<IReadOnlyList<RepoPath>> ClearIdleConeProjectsAsync(CancellationToken cancellationToken)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var shed = Views.ClearIdleConeProjects();
            if (shed.Count > 0)
            {
                await Projects.UnloadAsync(shed, cancellationToken).ConfigureAwait(false);
                Views.SyncDerivedFromLoader();
            }

            PublishLocked();
            return shed;
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Applies all deltas in this overlay onto a clean HEAD base <paramref name="baseSolution"/>.</summary>
    public async Task<Solution> ApplyDeltasAsync(Solution baseSolution, CancellationToken cancellationToken)
    {
        var snapshot = Deltas;
        var result = baseSolution;
        foreach (var (path, text) in snapshot)
            result = await Views.WithContentAsync(result, path, text, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Builds an on-demand transient HEAD baseline solution that releases all Roslyn compilations when disposed.</summary>
    public Task<SolutionViews.TransientBaseline> BuildTransientBaselineAsync(IReadOnlyCollection<ProjectNode> projects, CancellationToken cancellationToken) =>
        Views.BuildTransientBaselineAsync(projects, cancellationToken);

    /// <summary>Drops session views and any cached baseline compilations while trimming transient delta entries.</summary>
    public void TrimTransientCaches(bool clearSessionViews = true, bool dropCompiledBaseline = true)
    {
        Views.ClearTransientCaches(clearSessionViews, dropCompiledBaseline);
        Projects.TrimCaches();
        lock (_deltaLock)
        {
            var dirty = new HashSet<RepoPath>(Tracker.Changed);
            foreach (var stale in _deltas.Keys.Where(k => !dirty.Contains(k)).ToList())
                _deltas.Remove(stale);
            _dirtySet = dirty;
        }
    }

    /// <inheritdoc cref="SolutionViews.HeadText"/>
    public SourceText? HeadText(RepoPath path) => Views.HeadText(path);

    /// <inheritdoc cref="SolutionViews.CurrentWithHeadContentAsync(IEnumerable{RepoPath}, CancellationToken)"/>
    public Task<Solution> CurrentWithHeadContentAsync(IEnumerable<RepoPath> paths, CancellationToken cancellationToken) =>
        Views.CurrentWithHeadContentAsync(paths, cancellationToken);

    /// <summary>Derives a view from <paramref name="generation"/> with <paramref name="paths"/> at HEAD content.</summary>
    public Task<Solution> CurrentWithHeadContentAsync(Generation generation, IEnumerable<RepoPath> paths, CancellationToken cancellationToken) =>
        Views.CurrentWithHeadContentAsync(generation.Current, paths, cancellationToken);

    private HashSet<RepoPath> GetDirtyPaths()
    {
        lock (_deltaLock)
            return _dirtySet;
    }

    private void RefreshAllDeltas()
    {
        var changed = Tracker.Changed;
        var changedSet = new HashSet<RepoPath>(changed);
        lock (_deltaLock)
        {
            foreach (var stale in _deltas.Keys.Where(k => !changedSet.Contains(k)).ToList())
                _deltas.Remove(stale);
            foreach (var path in changed)
                _deltas[path] = ReadWorkingTreeText(path);
            _dirtySet = changedSet;
        }
    }

    private void RefreshDeltaPaths(IReadOnlyCollection<RepoPath> paths)
    {
        if (paths.Count == 0)
            return;
        var tracked = new HashSet<RepoPath>(Tracker.Changed);
        lock (_deltaLock)
        {
            var nextDirty = new HashSet<RepoPath>(_dirtySet);
            foreach (var path in paths)
            {
                var diskText = ReadWorkingTreeText(path);
                if (tracked.Contains(path) || DiffersFromHead(path, diskText))
                {
                    _deltas[path] = diskText;
                    nextDirty.Add(path);
                }
                else
                {
                    _deltas.Remove(path);
                    nextDirty.Remove(path);
                }
            }

            _dirtySet = nextDirty;
        }
    }

    private bool DiffersFromHead(RepoPath path, SourceText? diskText)
    {
        var headText = Views.HeadText(path);
        if (headText is null && diskText is null)
            return false;
        if (headText is null || diskText is null)
            return true;
        return !headText.ContentEquals(diskText);
    }

    private SourceText? ReadWorkingTreeText(RepoPath path)
    {
        if ((Solution?)Views.Current is { } current)
        {
            var ids = current.GetDocumentIdsWithFilePath(path.Absolute);
            if (!ids.IsEmpty)
            {
                var doc = (TextDocument?)current.GetDocument(ids[0]) ?? current.GetAdditionalDocument(ids[0]);
                if (doc is not null && doc.TryGetText(out var existing))
                    return existing;
            }
        }

        try
        {
            return File.Exists(path.Absolute) ? SolutionViews.Decode(SharedRead.AllBytes(path.Absolute)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Generation PublishLocked()
    {
        var current = Views.Current;
        var baseline = Views.Baseline;
        var graph = Projects.Graph;
        var changed = Tracker.Changed;
        var nonSources = Tracker.NonSources;
        var head = Tracker.Head;
        var configuration = Projects.ConfigurationGeneration;
        var baselineGeneration = Views.BaselineGeneration;
        if (_generation is { } published
            && ReferenceEquals(published.Current, current)
            && ReferenceEquals(published.Baseline, baseline)
            && ReferenceEquals(published.Graph, graph)
            && published.Head == head
            && published.ConfigurationGeneration == configuration
            && published.BaselineGeneration == baselineGeneration
            && published.Changed.SequenceEqual(changed)
            && published.NonSources.SequenceEqual(nonSources))
            return published;

        var next = new Generation(
            _generationNumber++,
            head,
            current,
            baseline,
            graph,
            [.. changed],
            [.. nonSources],
            Tracker.NonSourceChanged,
            [.. Tracker.NonSourceSample],
            configuration,
            baselineGeneration);
        Volatile.Write(ref _generation, next);
        return next;
    }

    private Generation PublishInitial()
    {
        var empty = new AdhocWorkspace().CurrentSolution;
        var next = new Generation(
            _generationNumber++,
            Tracker.Head,
            (Solution?)Views.Current ?? empty,
            (Solution?)Views.Baseline ?? empty,
            Projects.Graph!,
            [.. Tracker.Changed],
            [.. Tracker.NonSources],
            Tracker.NonSourceChanged,
            [.. Tracker.NonSourceSample],
            Projects.ConfigurationGeneration,
            Views.BaselineGeneration);
        return Interlocked.CompareExchange(ref _generation, next, null) ?? next;
    }

    public void Dispose()
    {
        Projects.Dispose();
        Tracker.Dispose();
        _writer.Dispose();
    }
}
