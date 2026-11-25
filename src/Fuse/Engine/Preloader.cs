using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Workspace;

namespace Fuse.Engine;

/// <summary>
///     Loads, in the background, the projects that depend on projects with uncommitted changes. A declaration change
///     has to bind those dependents, and loading them is the slow part of a first check (seconds per project); doing
///     it while the agent is busy elsewhere keeps later checks fast. It extracts the whole batch in one MSBuild
///     session without any lock, then opens the batch under one hold of the load lock, one project at a time only
///     when a batch load fails. Requests take precedence: it holds the gate only while it chooses the batch. It
///     builds no compilation: a check builds what it binds.
/// </summary>
internal sealed class Preloader
{
    private readonly RepoWorkspace _workspace;
    private readonly RequestGate _gate;
    private readonly Action<string> _log;

    /// <summary>Makes starting a load one step, so two requests that finish together cannot both start one.</summary>
    private readonly Lock _scheduling = new();

    /// <summary>
    ///     The projects whose background load failed, each with the state it failed in. Only the one running load reads
    ///     and writes it.
    /// </summary>
    private readonly Dictionary<RepoPath, Attempt> _failed = [];

    private Task _running = Task.CompletedTask;

    /// <param name="workspace">The workspace the projects load into.</param>
    /// <param name="gate">The gate it holds only while the next project is chosen, never during a load; requests take a reader from the scheduler and never wait on it.</param>
    /// <param name="log">Where a load that was skipped or failed is written.</param>
    public Preloader(RepoWorkspace workspace, RequestGate gate, Action<string> log)
    {
        _workspace = workspace;
        _gate = gate;
        _log = log;
    }

    /// <summary>True while a background load runs.</summary>
    public bool IsRunning => !_running.IsCompleted;

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
            List<ProjectNode> batch;
            // The gate only protects choosing the batch; the extraction and the loads run outside it, so requests
            // are served while they run.
            await _gate.EnterAsync(RequestGate.Preload, shutdown).ConfigureAwait(false);
            try
            {
                var generation = _workspace.PinGeneration();
                var graph = generation.Graph;
                batch = generation.Changed
                    .SelectMany(graph.OwnersOf)
                    .SelectMany(graph.DependentsOf)
                    .DistinctBy(p => p.Path)
                    .Where(p => !_workspace.IsLoaded(p) && !(_failed.TryGetValue(p.Path, out var failed) && failed == AttemptOf(p, graph)))
                    .ToList();
            }
            finally
            {
                _gate.Exit();
            }

            if (batch.Count == 0)
                return;

            // One extraction for the whole batch, without any lock: extracting project by project would run one
            // ephemeral MSBuild session per project, while a request the batch overlaps only waits on the short
            // loads below, never on the extraction.
            try
            {
                await _workspace.EnsureExtractedAsync(batch, shutdown).ConfigureAwait(false);
            }
            catch (FuseException e)
            {
                // Tried again once the projects reload or a restore rewrites its closure's assets; until then a check that
                // needs one of the projects loads it itself and reports the failure.
                var graph = _workspace.PinGeneration().Graph;
                foreach (var project in batch)
                    _failed[project.Path] = AttemptOf(project, graph);
                _log($"preload of {batch.Count} project(s) skipped: {e.Message}");
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _log($"preload failed: {e}");
                return;
            }

            // One batch load for the whole batch: the extraction above already covered it, so this opens from
            // the cache under one hold of the load lock instead of one hold per project.
            var pending = batch.Where(p => !_workspace.IsLoaded(p)).ToList();
            if (pending.Count > 0 && !shutdown.IsCancellationRequested)
            {
                try
                {
                    await _workspace.PreloadAsync(pending, shutdown).ConfigureAwait(false);
                    foreach (var project in pending)
                        _failed.Remove(project.Path);
                    pending.Clear();
                }
                catch (FuseException)
                {
                    // Retried one project at a time below, so one unloadable project neither blocks the rest nor
                    // marks them failed.
                    pending = pending.Where(p => !_workspace.IsLoaded(p)).ToList();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    _log($"preload failed: {e}");
                    return;
                }
            }

            foreach (var project in pending)
            {
                if (shutdown.IsCancellationRequested)
                    return;
                if (_workspace.IsLoaded(project))
                    continue;
                var attempt = AttemptOf(project, _workspace.PinGeneration().Graph);
                try
                {
                    await _workspace.PreloadAsync(project, shutdown).ConfigureAwait(false);
                    _failed.Remove(project.Path);
                }
                catch (FuseException e)
                {
                    // Tried again once the projects reload or a restore rewrites its closure's assets; until then a check that
                    // needs the project loads it itself and reports the failure.
                    _failed[project.Path] = attempt;
                    _log($"preload of {project.Name} skipped: {e.Message}");
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    _log($"preload failed: {e}");
                    return;
                }
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
