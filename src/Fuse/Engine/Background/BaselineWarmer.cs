using Fuse.Check.Pipeline;
using Fuse.Engine.Scheduling;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Workspace.Generations;
namespace Fuse.Engine.Background;

/// <summary>
///     Fills, in the background and one project at a time, the disk store with HEAD's errors after HEAD
///     moved, so the first check that needs them reads the store instead of binding HEAD. Each project it fills is
///     released when its errors are stored, so the warm holds no baseline compilation. For the same project it
///     also emits the project's summary when the project is outside the change cone, which is the one place a
///     summary is emitted: off the request path, at this background priority and never under the writer lock,
///     so a sync only ever reads a summary that is already kept. For the same project it also indexes the token
///     search entries a later reference search narrows by, so a search never builds the index on the request path.
///     Only loaded projects are filled; the warm loads none itself. Each project's warm-up runs inside a background
///     lane lease on the generation's background token with a 30 s analyzer budget, so interactive and command
///     checks preempt it and a newer generation or a memory shed stops it. Requests take precedence: it holds the gate only while it chooses a project, between
///     projects.
/// </summary>
internal sealed class BaselineWarmer
{
    private readonly RepoWorkspace _workspace;
    private readonly Checker _checker;
    private readonly RequestGate _gate;
    private readonly Scheduler _scheduler;
    private readonly MemoryBudget _budget;
    private readonly Action<string> _log;

    /// <summary>Makes starting a warm run one step, so two requests that finish together cannot both start one.</summary>
    private readonly Lock _scheduling = new();

    private Task _running = Task.CompletedTask;

    /// <summary>The generation <see cref="_warmedProjects"/> belongs to. Only the one running warm reads and writes both.</summary>
    private int _warmedGeneration = -1;

    /// <summary>The projects warmed for <see cref="_warmedGeneration"/>, so a project that loads after the warm still gets warmed.</summary>
    private readonly HashSet<RepoPath> _warmedProjects = [];

    private readonly Lock _runLock = new();
    private CancellationTokenSource? _current;

    /// <param name="workspace">The workspace whose baseline generations are warmed.</param>
    /// <param name="checker">The checker that warms one project.</param>
    /// <param name="gate">The gate it holds only while the next project is chosen, never during a warm; requests take a reader from the scheduler and never wait on it.</param>
    /// <param name="scheduler">The scheduler whose background lane the warm holds and whose background token stops it when a newer generation is published.</param>
    /// <param name="budget">The memory budget whose shed stops the warm.</param>
    /// <param name="log">Where each warmed project and each skipped run is written.</param>
    public BaselineWarmer(RepoWorkspace workspace, Checker checker, RequestGate gate, Scheduler scheduler, MemoryBudget budget, Action<string> log)
    {
        _workspace = workspace;
        _checker = checker;
        _gate = gate;
        _scheduler = scheduler;
        _budget = budget;
        _log = log;
    }

    /// <summary>
    ///     Makes a warm that runs on its own scheduler, for the tests that warm without a router. The engine itself
    ///     always uses the call with a scheduler, so its warm holds a background lane and stops on a newer generation.
    /// </summary>
    public BaselineWarmer(RepoWorkspace workspace, Checker checker, RequestGate gate, Action<string> log)
        : this(workspace, checker, gate, new Scheduler(Scheduler.DefaultReaderCount), new MemoryBudget(), log)
    {
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

    /// <summary>Stops the project that is warming, when the memory budget sheds background work at a request boundary.</summary>
    public void CancelCurrent()
    {
        lock (_runLock)
        {
            _current?.Cancel();
        }
    }

    private async Task RunAsync(CancellationToken shutdown)
    {
        var generation = -1;
        while (!shutdown.IsCancellationRequested)
        {
            ProjectNode? next;
            // The gate only protects choosing the next project; the warm itself runs outside it, so requests are served while a project warms.
            await _gate.EnterAsync(RequestGate.Preload, shutdown).ConfigureAwait(false);
            try
            {
                generation = _workspace.PinGeneration().BaselineGeneration;
                if (generation != _warmedGeneration)
                {
                    _warmedGeneration = generation;
                    _warmedProjects.Clear();
                }

                next = null;
                foreach (var candidate in _workspace.PinGeneration().Graph.Projects)
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
                _gate.Exit();
            }

            if (next is null)
            {
                _checker.SaveSearchIndex();
                return;
            }

            if (_budget.Assess().Shed.Contains(MemoryBudget.ShedStep.Background))
            {
                _log("baseline warm: skipped, memory shed");
                return;
            }

            _warmedProjects.Add(next.Path);
            var warming = System.Diagnostics.Stopwatch.GetTimestamp();
            using var projectSource = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _scheduler.BackgroundToken);
            lock (_runLock)
            {
                _current = projectSource;
            }

            try
            {
                SchedulerLease? lease;
                try
                {
                    lease = await _scheduler.EnterAsync(SchedulerLane.Background, "baseline warm", waitMs: 0, projectSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                using (lease)
                {
                    try
                    {
                        if (await _workspace.EmitSummaryAsync(next, projectSource.Token).ConfigureAwait(false))
                            _log($"summary emitted: {next.Name}");
                        if (await _checker.WarmBaselineAsync(next, projectSource.Token).ConfigureAwait(false))
                            _log($"baseline warm: {next.Name} compilation-wide analyzers done in {(System.Diagnostics.Stopwatch.GetTimestamp() - warming) * 1000 / System.Diagnostics.Stopwatch.Frequency} ms");
                        await _checker.WarmSearchIndexAsync(next, projectSource.Token).ConfigureAwait(false);
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
                }
            }
            finally
            {
                lock (_runLock)
                {
                    if (ReferenceEquals(_current, projectSource))
                        _current = null;
                }

                projectSource.Dispose();
            }

            if (_workspace.PinGeneration().BaselineGeneration != generation)
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
