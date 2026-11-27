using System.Collections.Concurrent;
using System.Runtime;
using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Repo.Tracking;
using Fuse.Workspace.Blueprints;
using Fuse.Workspace.Views;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Workspace.Generations;

/// <summary>
///     Shared hypervisor workspace for one git repository (<see cref="RepoRoot.GitCommonDirectory"/>). Holds one
///     shared immutable HEAD base layer (<see cref="BlueprintWorkspace.SharedResources"/>, <see cref="SummaryCache"/>,
///     and stored HEAD baselines) and one <see cref="WorktreeOverlay"/> per registered worktree so multiple git
///     worktrees share one process and keep only per-worktree deltas in memory.
/// </summary>
internal sealed class RepoWorkspace : IDisposable
{
    private static readonly Lock _liveLock = new();
    private static readonly List<WeakReference<RepoWorkspace>> _live = [];

    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, WorktreeOverlay> _overlays = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _registerLock = new(1, 1);

    public RepoWorkspace(RepoRoot root, Action<string> log)
    {
        Root = root;
        _log = log;
        PrimaryOverlay = new WorktreeOverlay(root, log);
        lock (_liveLock)
        {
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                if (!_live[i].TryGetTarget(out _))
                    _live.RemoveAt(i);
            }

            _live.Add(new WeakReference<RepoWorkspace>(this));
        }
    }

    /// <summary>The primary worktree overlay opened when this workspace was created.</summary>
    public WorktreeOverlay PrimaryOverlay { get; }

    /// <summary>Shared immutable metadata references, analyzers, and HEAD texts shared across every worktree overlay.</summary>
    public BlueprintWorkspace.SharedResources SharedResources => PrimaryOverlay.SharedResources;

    /// <summary>Snapshot of all worktree overlays registered on this hypervisor, primary first.</summary>
    public IReadOnlyCollection<WorktreeOverlay> Worktrees => [PrimaryOverlay, .. _overlays.Values];

    public ChangeTracker Tracker => PrimaryOverlay.Tracker;

    public RepoGraph Graph => PrimaryOverlay.Graph;

    /// <summary>The loaded projects as they are on disk.</summary>
    public Solution Current => PrimaryOverlay.Current;

    /// <summary>The loaded projects with every changed file at its HEAD content.</summary>
    public Solution Baseline => PrimaryOverlay.Baseline;

    /// <inheritdoc cref="SolutionViews.BaselineGeneration"/>
    public int BaselineGeneration => PrimaryOverlay.BaselineGeneration;

    /// <inheritdoc cref="ProjectLoader.ConfigurationGeneration"/>
    public int ConfigurationGeneration => PrimaryOverlay.ConfigurationGeneration;

    /// <summary>The summaries the views supply references to projects outside the change cone from.</summary>
    public SummaryCache Summaries => PrimaryOverlay.Summaries;

    /// <summary>How many session views the views keep now; none in a single-agent engine, so its syncs maintain none.</summary>
    public int SessionViewCount => PrimaryOverlay.SessionViewCount;

    /// <inheritdoc cref="SolutionViews.EmitSummaryAsync"/>
    /// <remarks>It takes no writer lock and changes neither view, so the background warm runs it after a request, never inside one.</remarks>
    public Task<bool> EmitSummaryAsync(ProjectNode node, CancellationToken cancellationToken) =>
        PrimaryOverlay.EmitSummaryAsync(node, cancellationToken);

    /// <inheritdoc cref="SolutionViews.UseSummaries"/>
    public bool UseSummaries
    {
        get => PrimaryOverlay.UseSummaries;
        set => PrimaryOverlay.UseSummaries = value;
    }

    public RepoRoot Root { get; }

    public void Log(string message) => _log(message);

    /// <summary>
    ///     Registers or retrieves the <see cref="WorktreeOverlay"/> for <paramref name="worktreeRoot"/>, sharing this
    ///     hypervisor's immutable HEAD resources. Rejects roots from a different repository.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="worktreeRoot"/> belongs to a different repository pipe.</exception>
    public async Task<WorktreeOverlay> RegisterWorktreeAsync(RepoRoot worktreeRoot, CancellationToken cancellationToken)
    {
        if (!string.Equals(worktreeRoot.PipeName, Root.PipeName, StringComparison.Ordinal))
            throw new ArgumentException($"Worktree '{worktreeRoot.Path}' belongs to engine '{worktreeRoot.PipeName}', expected '{Root.PipeName}'.", nameof(worktreeRoot));

        if (string.Equals(worktreeRoot.StateDirectory, Root.StateDirectory, StringComparison.Ordinal))
            return PrimaryOverlay;

        if (_overlays.TryGetValue(worktreeRoot.StateDirectory, out var existing))
            return existing;

        await _registerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_overlays.TryGetValue(worktreeRoot.StateDirectory, out existing))
                return existing;

            var overlay = new WorktreeOverlay(worktreeRoot, _log, PrimaryOverlay.SharedResources)
            {
                UseSummaries = UseSummaries,
            };
            await overlay.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _overlays[worktreeRoot.StateDirectory] = overlay;
            _log($"registered worktree overlay: {worktreeRoot.Path}");
            return overlay;
        }
        finally
        {
            _registerLock.Release();
        }
    }

    /// <summary>Returns the registered <see cref="WorktreeOverlay"/> for <paramref name="worktreeRoot"/>, or null when unregistered.</summary>
    public WorktreeOverlay? GetWorktree(RepoRoot worktreeRoot)
    {
        if (string.Equals(worktreeRoot.StateDirectory, Root.StateDirectory, StringComparison.Ordinal))
            return PrimaryOverlay;
        return _overlays.TryGetValue(worktreeRoot.StateDirectory, out var overlay) ? overlay : null;
    }

    /// <summary>Attempts to retrieve the registered overlay for <paramref name="worktreeRoot"/>.</summary>
    public bool TryGetWorktree(RepoRoot worktreeRoot, out WorktreeOverlay? overlay)
    {
        overlay = GetWorktree(worktreeRoot);
        return overlay is not null;
    }

    /// <summary>Unregisters and disposes a secondary worktree overlay when its session ends or worktree is deleted.</summary>
    /// <returns>True when a secondary overlay was removed; false for the primary overlay or unknown roots.</returns>
    public bool UnregisterWorktree(RepoRoot worktreeRoot)
    {
        if (string.Equals(worktreeRoot.StateDirectory, Root.StateDirectory, StringComparison.Ordinal))
            return false;
        if (!_overlays.TryRemove(worktreeRoot.StateDirectory, out var overlay))
            return false;
        overlay.Dispose();
        return true;
    }

    /// <summary>
    ///     The newest published generation. A check pins it when it starts work and holds it to the end, so the
    ///     views, graph and changed set it reads belong at one instant.
    /// </summary>
    public Generation PinGeneration() => PrimaryOverlay.PinGeneration();

    /// <summary>Evaluates the project graph and starts tracking changes. Compiles nothing.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken) =>
        PrimaryOverlay.InitializeAsync(cancellationToken);

    /// <inheritdoc cref="WorkspaceSync.SyncAsync"/>
    public Task SyncAsync(IEnumerable<RepoPath> knownPaths, CancellationToken cancellationToken) =>
        PrimaryOverlay.SyncAsync(knownPaths, cancellationToken);

    /// <summary>Syncs the workspace under the writer lock and pins the generation that sync published.</summary>
    /// <param name="knownPaths">Files just written by the agent, checked even before their watcher event arrives.</param>
    /// <param name="cancellationToken">Cancels re-evaluation.</param>
    /// <param name="confirmWithDisk">True to confirm the changed set against disk when the watcher reported nothing, for a check of every change.</param>
    public Task<Generation> SyncAndPinAsync(IEnumerable<RepoPath> knownPaths, CancellationToken cancellationToken, bool confirmWithDisk = false) =>
        PrimaryOverlay.SyncAndPinAsync(knownPaths, cancellationToken, confirmWithDisk);

    /// <summary>Loads <paramref name="projects"/> and everything they reference, and makes them part of both views.</summary>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public Task EnsureLoadedAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken) =>
        PrimaryOverlay.EnsureLoadedAsync(projects, cancellationToken);

    /// <summary>Loads <paramref name="projects"/> under the writer lock and pins the generation that load published.</summary>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public Task<Generation> EnsureLoadedAndPinAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken) =>
        PrimaryOverlay.EnsureLoadedAndPinAsync(projects, cancellationToken);

    /// <summary>Applies each of <paramref name="paths"/> to both views again: its disk content to <see cref="Current"/> and its HEAD content to <see cref="Baseline"/>.</summary>
    /// <param name="paths">The files to apply; each is also touched.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public Task PatchViewsAsync(IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken) =>
        PrimaryOverlay.PatchViewsAsync(paths, cancellationToken);

    /// <summary>Applies each of <paramref name="paths"/> under the writer lock and pins the generation it published.</summary>
    /// <param name="paths">The files to apply; each is also touched.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public Task<Generation> PatchAndPinAsync(IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken) =>
        PrimaryOverlay.PatchAndPinAsync(paths, cancellationToken);

    /// <summary>Derives both views from the loader's solution again, re-applying every touched and every changed file.</summary>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public Task RebuildViewsAsync(CancellationToken cancellationToken) =>
        PrimaryOverlay.RebuildViewsAsync(cancellationToken);

    /// <summary>Derives both views again under the writer lock and pins the generation it published, for a reader whose pinned generation lacks a reached project.</summary>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public Task<Generation> RebuildAndPinAsync(CancellationToken cancellationToken) =>
        PrimaryOverlay.RebuildAndPinAsync(cancellationToken);

    /// <summary>
    ///     Loads <paramref name="project"/> into the loader without touching <see cref="Current"/> or
    ///     <see cref="Baseline"/>, so it can run while requests are served; the next writer round folds it into both
    ///     views.
    /// </summary>
    /// <exception cref="FuseException">The project has not been restored or failed to load.</exception>
    public Task PreloadAsync(ProjectNode project, CancellationToken cancellationToken) =>
        PrimaryOverlay.PreloadAsync(project, cancellationToken);

    /// <summary>
    ///     Loads <paramref name="projects"/> into the loader (see <see cref="PreloadAsync(ProjectNode, CancellationToken)"/>),
    ///     in one batch under one hold of the load lock.
    /// </summary>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public Task PreloadAsync(IReadOnlyCollection<ProjectNode> projects, CancellationToken cancellationToken) =>
        PrimaryOverlay.PreloadAsync(projects, cancellationToken);

    /// <summary>
    ///     Extracts the blueprints covering <paramref name="projects"/> and their closures into the shared cache,
    ///     without opening anything, so a later load opens from the cache without extracting again.
    /// </summary>
    /// <exception cref="FuseException">A project has not been restored and no worktree blueprint covers it.</exception>
    public Task EnsureExtractedAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken) =>
        PrimaryOverlay.EnsureExtractedAsync(projects, cancellationToken);

    public bool IsLoaded(ProjectNode node) => PrimaryOverlay.IsLoaded(node);

    /// <summary>Why <paramref name="node"/> did not load, or null when no load of it has failed.</summary>
    public string? LoadFailureOf(ProjectNode node) => PrimaryOverlay.LoadFailureOf(node);

    /// <summary>The loaded Roslyn projects (one per target framework) built from <paramref name="node"/>.</summary>
    /// <remarks>
    ///     A Roslyn project keeps the path MSBuild evaluated, which is the node's own spelling, so the paths are compared
    ///     as spelled; this runs once per project of the solution for each reached project, on every check.
    /// </remarks>
    public static IEnumerable<Project> ProjectsFor(Solution solution, ProjectNode node) =>
        solution.Projects.Where(p => node.Path.Matches(p.FilePath));

    /// <summary>Sheds the session views under the writer lock across every registered overlay.</summary>
    /// <param name="cancellationToken">Cancels waiting for the writer lock.</param>
    public async Task ClearSessionViewsAsync(CancellationToken cancellationToken)
    {
        foreach (var overlay in Worktrees)
            await overlay.ClearSessionViewsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sheds the HEAD compilations under the writer lock across every registered overlay.</summary>
    /// <param name="cancellationToken">Cancels waiting for the writer lock.</param>
    public async Task ClearHeadCompilationsAsync(CancellationToken cancellationToken)
    {
        foreach (var overlay in Worktrees)
            await overlay.ClearHeadCompilationsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sheds idle cone projects under the writer lock across every registered overlay.</summary>
    /// <param name="cancellationToken">Cancels waiting for the writer lock.</param>
    public async Task ClearIdleConeProjectsAsync(CancellationToken cancellationToken)
    {
        foreach (var overlay in Worktrees)
            await overlay.ClearIdleConeProjectsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Builds a transient HEAD compilation view for <paramref name="projects"/> and their dependencies directly from
    ///     git HEAD blobs so BaselineStore misses release 100% of baseline Roslyn compilations on disposal.
    /// </summary>
    public Task<SolutionViews.TransientBaseline> BuildTransientBaselineAsync(IReadOnlyCollection<ProjectNode> projects, CancellationToken cancellationToken) =>
        PrimaryOverlay.BuildTransientBaselineAsync(projects, cancellationToken);

    /// <summary>
    ///     Drops transient caches across every worktree overlay, requests large-object-heap compaction, and runs
    ///     blocking compacting Gen2 garbage collection to immediately return unused managed heap back to the OS.
    /// </summary>
    public void YieldMemory(bool clearSessionViews = true, bool dropCompiledBaseline = true)
    {
        foreach (var overlay in Worktrees)
            overlay.TrimTransientCaches(clearSessionViews, dropCompiledBaseline);

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>
    ///     Sheds optional resident views across all worktrees under their writer locks and then runs
    ///     <see cref="YieldMemory"/> to compact heaps back to the OS (for <c>fuse slim</c> and pressure relief).
    /// </summary>
    public async Task YieldMemoryAsync(bool shedHeadCompilations = true, bool shedIdleConeProjects = true, CancellationToken cancellationToken = default)
    {
        foreach (var overlay in Worktrees)
        {
            await overlay.ClearSessionViewsAsync(cancellationToken).ConfigureAwait(false);
            if (shedHeadCompilations)
                await overlay.ClearHeadCompilationsAsync(cancellationToken).ConfigureAwait(false);
            if (shedIdleConeProjects)
                await overlay.ClearIdleConeProjectsAsync(cancellationToken).ConfigureAwait(false);
        }

        YieldMemory(clearSessionViews: true, dropCompiledBaseline: true);
    }

    /// <summary>
    ///     Drops the cheaply rebuilt caches of every overlay of this workspace, the shared HEAD texts and the
    ///     metadata references no live project holds, so an idle engine returns that memory; the views and their
    ///     compilations stay warm for the next request.
    /// </summary>
    public void ReleaseIdle()
    {
        foreach (var overlay in Worktrees)
            overlay.ReleaseIdle();
    }

    /// <summary>Releases every live workspace's cheaply rebuilt caches, so an idle engine returns that memory.</summary>
    public static void ReleaseIdleAll()
    {
        List<RepoWorkspace> targets;
        lock (_liveLock)
        {
            targets = [];
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i].TryGetTarget(out var target))
                    targets.Add(target);
                else
                    _live.RemoveAt(i);
            }
        }

        foreach (var target in targets)
            target.ReleaseIdle();
    }

    /// <inheritdoc cref="SolutionViews.HeadText"/>
    public SourceText? HeadText(RepoPath path) => PrimaryOverlay.HeadText(path);

    /// <inheritdoc cref="SolutionViews.CurrentWithHeadContentAsync(IEnumerable{RepoPath}, CancellationToken)"/>
    public Task<Solution> CurrentWithHeadContentAsync(IEnumerable<RepoPath> paths, CancellationToken cancellationToken) =>
        PrimaryOverlay.CurrentWithHeadContentAsync(paths, cancellationToken);

    /// <summary>
    ///     <paramref name="generation"/>'s current view with each of <paramref name="paths"/> at its HEAD content, for a
    ///     reader that pinned that generation. Neither published view changes.
    /// </summary>
    public Task<Solution> CurrentWithHeadContentAsync(Generation generation, IEnumerable<RepoPath> paths, CancellationToken cancellationToken) =>
        PrimaryOverlay.CurrentWithHeadContentAsync(generation, paths, cancellationToken);

    public void Dispose()
    {
        PrimaryOverlay.Dispose();
        foreach (var overlay in _overlays.Values)
            overlay.Dispose();
        _overlays.Clear();
        _registerLock.Dispose();
        lock (_liveLock)
        {
            for (var i = _live.Count - 1; i >= 0; i--)
            {
                if (!_live[i].TryGetTarget(out var target) || ReferenceEquals(target, this))
                    _live.RemoveAt(i);
            }
        }
    }
}
