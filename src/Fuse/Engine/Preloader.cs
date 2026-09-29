using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Telemetry;
using Fuse.Workspace;

namespace Fuse.Engine;

/// <summary>
///     Loads, in the background and one at a time, the projects that depend on projects with uncommitted changes.
///     A declaration change has to bind those dependents, and loading them is the slow part of a first check
///     (seconds per project); doing it while the agent is busy elsewhere keeps later checks fast. Requests take
///     precedence: the request lock admits them between project loads.
/// </summary>
internal sealed class Preloader
{
    private readonly RepoWorkspace _workspace;
    private readonly SemaphoreSlim _requestLock;
    private readonly EngineLog _log;

    /// <summary>Makes starting a load one step, so two requests that finish together cannot both start one.</summary>
    private readonly Lock _scheduling = new();

    /// <summary>
    ///     The projects whose background load failed, each with the state it failed in. Only the one running load reads
    ///     and writes it.
    /// </summary>
    private readonly Dictionary<RepoPath, Attempt> _failed = [];

    private Task _running = Task.CompletedTask;

    /// <param name="workspace">The workspace the projects load into.</param>
    /// <param name="requestLock">The request lock. It is held only while the next project is chosen, never during a load.</param>
    /// <param name="log">Where a load that was skipped or failed is written.</param>
    public Preloader(RepoWorkspace workspace, SemaphoreSlim requestLock, EngineLog log)
    {
        _workspace = workspace;
        _requestLock = requestLock;
        _log = log;
    }

    /// <summary>Starts loading in the background, unless a load is already running or the engine is shutting down.</summary>
    /// <param name="shutdown">The engine's shutdown token, which stops the load between two projects.</param>
    public void Schedule(CancellationToken shutdown)
    {
        lock (_scheduling)
        {
            if (!_running.IsCompleted || shutdown.IsCancellationRequested)
                return;
            // RunAsync checks the token between projects, so Task.Run is not given it.
            _running = Task.Run(() => RunAsync(shutdown), CancellationToken.None);
        }
    }

    private async Task RunAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            ProjectNode? next;
            // The request lock only protects choosing the next project; the load itself runs outside it, so requests are served while a project loads.
            await _requestLock.WaitAsync(shutdown).ConfigureAwait(false);
            try
            {
                var graph = _workspace.Graph;
                next = _workspace.Tracker.Changed
                    .SelectMany(graph.OwnersOf)
                    .SelectMany(graph.DependentsOf)
                    .FirstOrDefault(p => !_workspace.IsLoaded(p) && !(_failed.TryGetValue(p.Path, out var failed) && failed == AttemptOf(p, graph)));
            }
            finally
            {
                _requestLock.Release();
            }

            if (next is null)
                return;
            var attempt = AttemptOf(next, _workspace.Graph);
            try
            {
                await _workspace.PreloadAsync(next, shutdown).ConfigureAwait(false);
                _failed.Remove(next.Path);
            }
            catch (FuseException e)
            {
                // Tried again once the projects reload or a restore rewrites its closure's assets; until then a check that
                // needs the project loads it itself and reports the failure.
                _failed[next.Path] = attempt;
                _log.Write($"preload of {next.Name} skipped: {e.Message}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _log.Write($"preload failed: {e}");
                return;
            }
        }
    }

    /// <summary>
    ///     Waits for a background load to stop, after the shutdown token has been cancelled, so the workspace is never
    ///     disposed under a project that is still loading.
    /// </summary>
    public async Task WaitAsync()
    {
        try
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
        }
    }

    /// <summary>
    ///     The state a load of <paramref name="project"/> starts from: the configuration generation, and the newest write
    ///     time of the restore output (<c>project.assets.json</c>) in its closure. A missing file reads as the earliest
    ///     time, so a restore that creates it changes the state.
    /// </summary>
    private Attempt AttemptOf(ProjectNode project, RepoGraph graph) => new(
        _workspace.ConfigurationGeneration,
        graph.ClosureOf(project).Max(p => File.GetLastWriteTimeUtc(p.AssetsFile.Absolute)));

    /// <summary>
    ///     What a failed load depended on. A project fails for the same reason while both stay the same, so it is tried
    ///     again only when one changed: the projects were evaluated again or reloaded, or a restore wrote its closure's
    ///     assets.
    /// </summary>
    /// <param name="ConfigurationGeneration">The workspace's <see cref="RepoWorkspace.ConfigurationGeneration"/>.</param>
    /// <param name="AssetsWrittenUtc">The newest write time of a <c>project.assets.json</c> in the project's closure.</param>
    private readonly record struct Attempt(int ConfigurationGeneration, DateTime AssetsWrittenUtc);
}
