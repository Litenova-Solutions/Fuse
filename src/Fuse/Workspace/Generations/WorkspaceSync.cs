using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Repo.Tracking;
using Fuse.Workspace.Blueprints;
using Fuse.Workspace.Views;
namespace Fuse.Workspace.Generations;

/// <summary>
///     Keeps both views in step with the working tree and with the projects the loader has open. Every request starts
///     with <see cref="SyncAsync"/>, which acts on the <see cref="SyncResult"/> the tracker returns, and opens what it
///     needs with <see cref="EnsureLoadedAsync"/>. Either derives both views again when the loader holds a project they
///     lack (<see cref="SolutionViews.HoldEveryOpenProject"/>).
/// </summary>
internal sealed class WorkspaceSync
{
    private readonly ChangeTracker _tracker;
    private readonly ProjectLoader _projects;
    private readonly SolutionViews _views;
    private readonly Action<string> _log;
    private readonly int _maxChurn;

    /// <summary>
    ///     The additional files the last sync patched because they differed from HEAD. One that matches HEAD again
    ///     leaves the tracker changed set, so the next sync patches it once more to bring its HEAD content back.
    /// </summary>
    private HashSet<RepoPath> _patchedAdditionalFiles = [];

    /// <summary>
    ///     The result of a sync whose action did not finish, because it was cancelled or failed, or null. The tracker
    ///     has already folded that sync's changes in (a moved HEAD is not reported again), so the next sync acts on this
    ///     result followed by its own (<see cref="SyncResult.Then"/>) instead of losing it.
    /// </summary>
    private SyncResult? _unfinished;

    /// <param name="tracker">The changes since HEAD.</param>
    /// <param name="projects">The loader that holds the open projects.</param>
    /// <param name="views">The current and baseline views.</param>
    /// <param name="log">Where a reload and its reason are logged.</param>
    /// <param name="maxChurn">
    ///     How many touched paths that no longer differ from HEAD the views may re-apply before a sync re-evaluates the
    ///     projects; <see cref="SolutionViews.MaxTouchedPaths"/> unless a test sets a smaller bound.
    /// </param>
    public WorkspaceSync(ChangeTracker tracker, ProjectLoader projects, SolutionViews views, Action<string> log, int maxChurn = SolutionViews.MaxTouchedPaths)
    {
        _tracker = tracker;
        _projects = projects;
        _views = views;
        _log = log;
        _maxChurn = maxChurn;
    }

    /// <summary>Evaluates every project, closes them all and empties both views. Compiles nothing.</summary>
    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        await _projects.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        _views.Clear();
    }

    /// <summary>Folds disk changes into both views, after the changes of an earlier sync that did not finish.</summary>
    /// <param name="knownPaths">Files just written by the agent, checked even before their watcher event arrives.</param>
    /// <param name="cancellationToken">Cancels re-evaluation.</param>
    /// <param name="confirmWithDisk">True to confirm the changed set against disk when the watcher reported nothing.</param>
    /// <exception cref="FuseException">
    ///     git or MSBuild failed. The result stays unfinished, so the next sync does the same work again.
    /// </exception>
    public async Task SyncAsync(IEnumerable<RepoPath> knownPaths, CancellationToken cancellationToken, bool confirmWithDisk = false)
    {
        var result = await _tracker.SyncAsync(knownPaths, cancellationToken, confirmWithDisk).ConfigureAwait(false);
        if (_unfinished is { } unfinished)
            result = unfinished.Then(result);
        if (result is SyncResult.Patch overflowing && _views.ChurnCount is var churn && churn > _maxChurn)
        {
            // Re-evaluates the projects as a project-file change does, so the paths a rebuild re-applies beyond the
            // changed set stay bounded however long the engine runs. Changed files never count: every rebuild applies
            // them, so a tree with more changed files than the bound does not re-evaluate on every sync. The churn
            // paths are left out: they match HEAD again and the re-evaluation reopens the projects from disk, so
            // re-applying them would grow the touched set past the bound again and re-evaluate on every sync.
            _log($"touched set passed {_maxChurn} paths that no longer differ from HEAD ({churn} of them, {_tracker.Changed.Count} changed file(s)); re-evaluating projects and resetting it");
            var paths = new HashSet<RepoPath>(_tracker.Changed);
            foreach (var directory in overflowing.VanishedDirectories)
                paths.UnionWith(_views.FilesUnder(directory));
            _views.ResetTouched();
            result = new SyncResult.Reevaluate(paths, $"touched set passed {_maxChurn} paths");
        }

        _unfinished = result;
        switch (result)
        {
            case SyncResult.Reevaluate reevaluate:
                await ReloadAsync(reevaluate, reevaluate.Trigger, cancellationToken).ConfigureAwait(false);
                break;
            case SyncResult.Reload reload:
                await ReloadAsync(reload, reload.Trigger, cancellationToken).ConfigureAwait(false);
                break;
            case SyncResult.Patch patch:
                await PatchAsync(patch, cancellationToken).ConfigureAwait(false);
                _unfinished = null;
                break;
        }

        // A build writes generated files in obj, which the watcher skips.
        await _views.RefreshGeneratedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads <paramref name="projects"/> and everything they reference, and makes them part of both views.</summary>
    /// <remarks>
    ///     Both views are derived again whenever the loader holds a project they lack, whether this call opened it or
    ///     not: a background load, or a load an earlier request opened before it was cancelled, leaves such a project,
    ///     and one rebuild folds in all of them.
    /// </remarks>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public async Task EnsureLoadedAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken)
    {
        var requested = projects as IReadOnlyList<ProjectNode> ?? [.. projects];
        var needsUnshedRebuild = requested.Count > 0
            && _views.Unshed(requested.SelectMany(_projects.Graph.ClosureOf).Select(p => p.Path));
        await _projects.LoadAsync(requested, cancellationToken).ConfigureAwait(false);
        if (!_views.HoldEveryOpenProject || needsUnshedRebuild)
            await _views.RebuildAsync(cancellationToken).ConfigureAwait(false);
        else if (_views.IsBaselineShed && requested.Count > 0)
            await _views.MaterializeBaselineShellAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies each of <paramref name="paths"/> to both views again: its disk content to the current view and its HEAD content to the baseline.</summary>
    /// <param name="paths">The files to apply; each is also touched.</param>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public Task PatchViewsAsync(IReadOnlyCollection<RepoPath> paths, CancellationToken cancellationToken) =>
        _views.PatchAsync(paths, cancellationToken);

    /// <summary>Derives both views from the loader's solution again, re-applying every touched and every changed file.</summary>
    /// <param name="cancellationToken">Cancels reading file contents.</param>
    public Task RebuildAsync(CancellationToken cancellationToken) =>
        _views.RebuildAsync(cancellationToken);

    /// <summary>
    ///     Acts on <see cref="SyncResult.Reevaluate"/> and <see cref="SyncResult.Reload"/>, which differ only in whether
    ///     the projects are evaluated before every project is closed.
    /// </summary>
    /// <remarks>
    ///     The result is finished once every project is closed and both views are empty: from then on the graph, the
    ///     loader and the views agree with the working tree and HEAD, and any load derives both views again. Reopening
    ///     the projects that were loaded only saves the next check a cold load, so a reopen that is cut short leaves
    ///     nothing to redo.
    /// </remarks>
    private async Task ReloadAsync(SyncResult result, string trigger, CancellationToken cancellationToken)
    {
        _log($"reloading: {result.GetType().Name} trigger={trigger}");
        var reopen = _projects.LoadedPaths();
        _views.Touch(result.Paths);
        if (result is SyncResult.Reevaluate)
            await _projects.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        else
            await _projects.ResetAsync(cancellationToken).ConfigureAwait(false);
        // The loader reads the additional files from disk when it opens their projects, but the baseline
        // holds their HEAD content only when a rebuild applies them, so they are touched like the sources.
        _views.Touch(ChangedAdditionalFiles());
        _views.Clear();
        _unfinished = null;
        // Reopen the projects that were loaded, so the next check does not pay for a cold load it did not ask for.
        var nodes = reopen.Select(_projects.Graph.Find).OfType<ProjectNode>().ToList();
        if (nodes.Count > 0)
            await EnsureLoadedAsync(nodes, cancellationToken).ConfigureAwait(false);
    }

    private async Task PatchAsync(SyncResult.Patch patch, CancellationToken cancellationToken)
    {
        if (!_views.HoldEveryOpenProject)
        {
            // The loader holds a project neither view has: derive both from it again, with the patched files touched so
            // the rebuild applies them too.
            _views.Touch(patch.Paths);
            await _views.RebuildAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (_views.IsBaselineShed)
        {
            // The baseline view was shed without invalidating loader derivation, so no rebuild follows it: materialize
            // its shell from the current view, and both views hold every open project again, as after a rebuild.
            await _views.MaterializeBaselineShellAsync(cancellationToken).ConfigureAwait(false);
        }

        var paths = new HashSet<RepoPath>(patch.Paths);
        foreach (var directory in patch.VanishedDirectories)
            paths.UnionWith(_views.FilesUnder(directory));
        // The patch carries only sources, but the tracker also reports the non-sources it saw change, and an
        // edit to an additional file reaches the analyzers only when both views patch it like a source.
        var additional = ChangedAdditionalFiles().ToHashSet();
        paths.UnionWith(additional);
        paths.UnionWith(_patchedAdditionalFiles);
        await _views.PatchAsync(paths, cancellationToken).ConfigureAwait(false);
        _patchedAdditionalFiles = additional;
    }

    /// <summary>The additional files that differ from HEAD, in no particular order.</summary>
    private IReadOnlyList<RepoPath> ChangedAdditionalFiles() =>
        [.. _tracker.NonSources.Where(_projects.Graph.IsAdditionalFile)];
}
