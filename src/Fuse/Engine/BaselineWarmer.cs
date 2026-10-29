using Fuse.Check;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Workspace;

namespace Fuse.Engine;

/// <summary>
///     Warms, in the background and one project at a time, the baseline compilation-wide analyzer results after HEAD
///     moved, so the first check that needs them finds them running or done instead of starting them. Only loaded
///     projects are warmed; the warm loads none itself. Requests take precedence: the request lock admits them between
///     projects.
/// </summary>
internal sealed class BaselineWarmer
{
    private readonly RepoWorkspace _workspace;
    private readonly Checker _checker;
    private readonly RequestGate _requestLock;
    private readonly Action<string> _log;

    /// <summary>Makes starting a warm run one step, so two requests that finish together cannot both start one.</summary>
    private readonly Lock _scheduling = new();

    private Task _running = Task.CompletedTask;

    /// <summary>The generation <see cref="_warmedProjects"/> belongs to. Only the one running warm reads and writes both.</summary>
    private int _warmedGeneration = -1;

    /// <summary>The projects warmed for <see cref="_warmedGeneration"/>, so a project that loads after the warm still gets warmed.</summary>
    private readonly HashSet<RepoPath> _warmedProjects = [];

    /// <param name="workspace">The workspace whose baseline generations are warmed.</param>
    /// <param name="checker">The checker that warms one project.</param>
    /// <param name="requestLock">The request lock. It is held only while the next project is chosen, never during a warm.</param>
    /// <param name="log">Where each warmed project and each skipped run is written.</param>
    public BaselineWarmer(RepoWorkspace workspace, Checker checker, RequestGate requestLock, Action<string> log)
    {
        _workspace = workspace;
        _checker = checker;
        _requestLock = requestLock;
        _log = log;
    }

    /// <summary>True while a background warm runs.</summary>
    public bool IsRunning => !_running.IsCompleted;

    /// <summary>Starts warming in the background, unless a run is already going or the engine is shutting down.</summary>
    /// <param name="shutdown">The engine's shutdown token, which stops the warm between two projects.</param>
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
        var generation = -1;
        while (!shutdown.IsCancellationRequested)
        {
            ProjectNode? next;
            // The request lock only protects choosing the next project; the warm itself runs outside it, so requests are served while a project warms.
            await _requestLock.EnterAsync(RequestGate.Preload, shutdown).ConfigureAwait(false);
            try
            {
                generation = _workspace.BaselineGeneration;
                if (generation != _warmedGeneration)
                {
                    _warmedGeneration = generation;
                    _warmedProjects.Clear();
                }

                next = null;
                foreach (var candidate in _workspace.Graph.Projects)
                {
                    if (_workspace.IsLoaded(candidate) && !_warmedProjects.Contains(candidate.Path))
                    {
                        next = candidate;
                        break;
                    }
                }
            }
            finally
            {
                _requestLock.Exit();
            }

            if (next is null)
                return;

            _warmedProjects.Add(next.Path);
            var warming = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                if (await _checker.WarmBaselineAsync(next, shutdown).ConfigureAwait(false))
                    _log($"baseline warm: {next.Name} compilation-wide analyzers done in {(System.Diagnostics.Stopwatch.GetTimestamp() - warming) * 1000 / System.Diagnostics.Stopwatch.Frequency} ms");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _log($"baseline warm failed: {e}");
                return;
            }

            if (_workspace.BaselineGeneration != generation)
            {
                _log("baseline warm: skipped, HEAD moved again");
                return;
            }
        }
    }

    /// <summary>
    ///     Waits for a background warm to stop, after the shutdown token has been cancelled, so the workspace is never
    ///     disposed under a project that is still warming.
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
