using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Repo;

namespace Fuse.Workspace;

/// <summary>
///     Keeps both views in step with the working tree and with the projects the loader has open. Every request starts
///     with <see cref="SyncAsync"/>, which acts on the <see cref="SyncResult"/> the tracker returns, and opens what it
///     needs with <see cref="EnsureLoadedAsync"/>. Either derives both views again when the loader holds a project they
///     lack.
/// </summary>
internal sealed class WorkspaceSync
{
    private readonly ChangeTracker _tracker;
    private readonly ProjectLoader _projects;
    private readonly SolutionViews _views;
    private readonly Action<string> _log;

    public WorkspaceSync(ChangeTracker tracker, ProjectLoader projects, SolutionViews views, Action<string> log)
    {
        _tracker = tracker;
        _projects = projects;
        _views = views;
        _log = log;
    }

    /// <summary>Evaluates every project, closes them all and empties both views. Compiles nothing.</summary>
    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        await _projects.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        _views.Clear();
    }

    /// <summary>Folds disk changes into both views.</summary>
    /// <param name="knownPaths">Files just written by the agent, checked even before their watcher event arrives.</param>
    /// <param name="cancellationToken">Cancels re-evaluation.</param>
    public async Task SyncAsync(IEnumerable<RepoPath> knownPaths, CancellationToken cancellationToken)
    {
        var result = await _tracker.SyncAsync(knownPaths, cancellationToken).ConfigureAwait(false);
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
                break;
        }
    }

    /// <summary>Loads <paramref name="projects"/> and everything they reference, and makes them part of both views.</summary>
    /// <exception cref="FuseException">A project has not been restored or failed to load.</exception>
    public async Task EnsureLoadedAsync(IEnumerable<ProjectNode> projects, CancellationToken cancellationToken)
    {
        var opened = await _projects.LoadAsync(projects, cancellationToken).ConfigureAwait(false);
        // Taken whether or not this call opened a project, so one rebuild folds in a background load as well.
        var preloaded = _projects.TakePreloaded();
        if (opened || preloaded)
            await _views.RebuildAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Acts on <see cref="SyncResult.Reevaluate"/> and <see cref="SyncResult.Reload"/>, which differ only in whether
    ///     the projects are evaluated before every project is closed.
    /// </summary>
    private async Task ReloadAsync(SyncResult result, string trigger, CancellationToken cancellationToken)
    {
        _log($"reloading: {result.GetType().Name} trigger={trigger}");
        var reopen = _projects.LoadedPaths();
        _views.Touch(result.Paths);
        if (result is SyncResult.Reevaluate)
            await _projects.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        else
            await _projects.ResetAsync(cancellationToken).ConfigureAwait(false);
        _views.Clear();
        // Reopen the projects that were loaded, so the next check does not pay for a cold load it did not ask for.
        var nodes = reopen.Select(_projects.Graph.Find).OfType<ProjectNode>().ToList();
        if (nodes.Count > 0)
            await EnsureLoadedAsync(nodes, cancellationToken).ConfigureAwait(false);
    }

    private async Task PatchAsync(SyncResult.Patch patch, CancellationToken cancellationToken)
    {
        if (_projects.TakePreloaded())
        {
            // A background load added projects: derive both views from the loader again.
            _views.Touch(patch.Paths);
            await _views.RebuildAsync(cancellationToken).ConfigureAwait(false);
        }

        var paths = new HashSet<RepoPath>(patch.Paths);
        foreach (var directory in patch.VanishedDirectories)
            paths.UnionWith(_views.FilesUnder(directory));
        await _views.PatchAsync(paths, cancellationToken).ConfigureAwait(false);
    }
}
