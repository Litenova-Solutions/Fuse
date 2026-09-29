using Fuse.Failures;
using Fuse.Graph;
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
    private readonly SemaphoreSlim _gate;
    private readonly EngineLog _log;
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private Task _running = Task.CompletedTask;

    /// <param name="workspace">The workspace the projects load into.</param>
    /// <param name="gate">The request lock. It is held only while the next project is chosen, never during a load.</param>
    /// <param name="log">Where a load that was skipped or failed is written.</param>
    public Preloader(RepoWorkspace workspace, SemaphoreSlim gate, EngineLog log)
    {
        _workspace = workspace;
        _gate = gate;
        _log = log;
    }

    /// <summary>Starts loading in the background, unless a load is already running or the engine is shutting down.</summary>
    /// <param name="shutdown">The engine's shutdown token, which stops the load between two projects.</param>
    public void Schedule(CancellationToken shutdown)
    {
        if (!_running.IsCompleted || shutdown.IsCancellationRequested)
            return;
        // RunAsync checks the token between projects, so Task.Run is not given it.
        _running = Task.Run(() => RunAsync(shutdown), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            ProjectNode? next;
            // The gate only protects choosing the next project; the load itself runs outside it, so requests keep flowing.
            await _gate.WaitAsync(shutdown).ConfigureAwait(false);
            try
            {
                var graph = _workspace.Graph;
                next = _workspace.Tracker.Changed
                    .SelectMany(graph.OwnersOf)
                    .SelectMany(graph.DependentsOf)
                    .FirstOrDefault(p => !_workspace.IsLoaded(p) && !_failed.Contains(p.Path));
            }
            finally
            {
                _gate.Release();
            }

            if (next is null)
                return;
            try
            {
                await _workspace.PreloadAsync(next, shutdown).ConfigureAwait(false);
            }
            catch (FuseException e)
            {
                // Not tried again in the background; a check that needs the project loads it itself and reports the failure.
                _failed.Add(next.Path);
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
}
