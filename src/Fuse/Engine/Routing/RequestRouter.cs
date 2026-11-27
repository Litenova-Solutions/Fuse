using System.Diagnostics;
using Fuse.Check.Analyzers;
using Fuse.Check.Pipeline;
using Fuse.Check.Sessions;
using Fuse.Engine.Background;
using Fuse.Engine.Scheduling;
using Fuse.Engine.Server;
using Fuse.Failures;
using Fuse.Identity;
using Fuse.Judge;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Telemetry;
using Fuse.Testing.Planning;
using Fuse.Workspace.Generations;
namespace Fuse.Engine.Routing;

/// <summary>
///     Owns the warm workspace and answers requests: it initializes the workspace, admits requests through the
///     scheduler (<see cref="Scheduler"/>) to a bounded pool of readers, and routes each to the feature that answers it.
/// </summary>
internal sealed class RequestRouter : IDisposable
{
    private readonly RepoRoot _root;
    private readonly EngineLog _log;
    private readonly RepoWorkspace _workspace;
    private readonly TestPlanner _planner;
    private readonly RequestGate _requestLock = new();
    private readonly Scheduler _scheduler;
    private readonly bool _ownsScheduler;
    private readonly MemoryBudget _budget;
    private readonly Preloader _preloader;
    private readonly BaselineWarmer _warmer;
    private readonly RequestLog _requestLog;
    private readonly SessionEdits _sessions;
    private readonly AnswerStore _answers;
    private readonly BackgroundCheck _background;
    private readonly WatchHub? _watchHub;
    private readonly CheckRoute _checkRoute;
    private readonly TestPlanRoute _testPlanRoute;
    private readonly SessionRoute _sessionRoute;
    private readonly InspectionRoute _inspectionRoute;

    /// <summary>Guards the routers that serve the other worktrees of this repository.</summary>
    private readonly Lock _worktreeLock = new();

    private readonly Dictionary<string, RequestRouter> _worktreeRouters = new(StringComparer.Ordinal);
    private Task? _initialization;
    private CancellationToken _shutdown;

    public RequestRouter(RepoRoot root, EngineLog log, WatchHub? watchHub = null)
        : this(root, log, new Scheduler(Scheduler.DefaultReaderCount), new MemoryBudget(), watchHub)
    {
    }

    internal RequestRouter(RepoRoot root, EngineLog log, Scheduler scheduler, MemoryBudget budget, WatchHub? watchHub = null, bool ownsScheduler = true)
    {
        _root = root;
        _log = log;
        _workspace = new RepoWorkspace(root, log.Write);
        _sessions = new SessionEdits(root, Path.Combine(root.StateDirectory, "sessions.tsv"), Path.Combine(root.StateDirectory, "registered-sessions.tsv"), log: log.Write);
        var checker = new Checker(_workspace, _sessions);
        _planner = new TestPlanner(_workspace);
        _preloader = new Preloader(_workspace, _requestLock, log.Write);
        _warmer = new BaselineWarmer(_workspace, checker, _requestLock, log.Write);
        _requestLog = new RequestLog(log);
        _scheduler = scheduler;
        _ownsScheduler = ownsScheduler;
        _budget = budget;
        _answers = new AnswerStore(root.StateDirectory, log.Write);
        var guards = new SurfaceGuardStore(root);
        var judge = new CandidateJudge(_workspace, root);
        _checkRoute = new CheckRoute(_workspace, checker, _sessions, guards, watchHub, log, root, _answers, _requestLog);
        _testPlanRoute = new TestPlanRoute(_planner, _workspace, _sessions, log);
        _sessionRoute = new SessionRoute(_sessions, guards, watchHub, root, log);
        _background = new BackgroundCheck(_checkRoute.LiveSessionFiles, _checkRoute.BackgroundKey, _answers, RunBackgroundCheckAsync, log.Write);
        _inspectionRoute = new InspectionRoute(_workspace, scheduler, _requestLog, log, judge, budget, _answers, checker, _background, _preloader, _warmer);
        _watchHub = watchHub;
    }

    /// <summary>Evaluates projects, then preloads the projects that already have uncommitted changes.</summary>
    /// <param name="cancellationToken">The engine's shutdown token, which also stops the background load of dependents.</param>
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        _shutdown = cancellationToken;
        return _initialization ??= InitializeCoreAsync(cancellationToken);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await _requestLock.EnterAsync(RequestGate.Initialization, cancellationToken).ConfigureAwait(false);
        try
        {
            await _workspace.InitializeAsync(cancellationToken).ConfigureAwait(false);
            // A file an earlier engine recorded that matches HEAD now was reverted or committed while no engine watched;
            // forgotten here, its sessions are not still credited when the file is written again before the first check.
            _sessions.Forget(_workspace.PinGeneration().Changed);
            var live = _sessions.LiveRegistrations();
            if (live.Count > 0)
                _log.Write($"registered sessions live: {string.Join(", ", live.Select(r => SessionId.DisplayNameOf(r.Session)).Order(StringComparer.Ordinal))}");
            var initial = _workspace.PinGeneration();
            var owners = initial.Changed.SelectMany(initial.Graph.OwnersOf).DistinctBy(p => p.Path).ToList();
            if (owners.Count > 0)
            {
                try
                {
                    await _workspace.EnsureLoadedAsync(owners, cancellationToken).ConfigureAwait(false);
                }
                catch (FuseException e)
                {
                    _log.Write($"preload skipped: {e.Message}");
                }
            }

            _inspectionRoute.CaptureStatus(reloadMs: null);
        }
        finally
        {
            _requestLock.Exit();
        }

        _inspectionRoute.ScheduleBackground(_shutdown);
    }

    /// <summary>
    ///     Answers one request. A ping or a shutdown is answered at once; a status answer bypasses the scheduler
    ///     and reads the newest generation; a session registration or guard, a memory yield and a watch subscription
    ///     bypass it too, under their own locks. A check or a test plan waits for initialization
    ///     (a check that does not wait for the load is refused while loading),
    ///     then for a reader in its lane, then runs. A check whose content key matches one in flight shares it;
    ///     one whose key the result store already keeps is answered from it, stamped or not as it asked. Every
    ///     check answer is kept by its key and, for a session, as its session file, and every Unanswered and
    ///     Busy answer is logged with its kind, its code and its session. A request that will not start before its wait ends, and one past the memory ceiling, is answered Busy
    ///     at once. A check's wait and analyzer budget start when a reader takes it, not when it queued. A check
    ///     holds a reader until it is answered; a test plan holds one through the sync and the selection, and
    ///     releases it before it mirrors and emits from the snapshot it took. A changed-build plan syncs without loading a project.
    ///     A request that names another worktree of this repository is answered by that worktree's router, which this
    ///     router starts on its first request and which shares this router's scheduler, memory budget, log and watch hub.
    /// </summary>
    public async Task<EngineResponse> HandleAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (await RouteToWorktreeAsync(request, cancellationToken).ConfigureAwait(false) is { } routed)
            return routed;

        // The session a request names is recorded before its key is taken: the key hashes the session record, so
        // a repeat of the check that first named the session takes the same key as the check that computed the
        // answer and finds it kept, instead of hashing a record its own request had not written yet.
        _checkRoute.NoteSession(request);
        var (key, keyed) = await _checkRoute.PinKeyAsync(request, _initialization, cancellationToken).ConfigureAwait(false);

        if (key is not null && _checkRoute.TryAnswerKept(request, key) is { } kept)
            return kept;

        EngineResponse response;
        try
        {
            response = key is null
                ? await ScheduledAsync(request, cancellationToken).ConfigureAwait(false)
                : await _scheduler.RunCoalescedAsync(key, token => ScheduledAsync(request, token), cancellationToken).ConfigureAwait(false);
        }
        catch (SchedulerBusyException busy)
        {
            response = new EngineResponse.Busy(busy.Position, busy.EstimatedWaitMs);
            _requestLog.WriteUnanswered(request, response);
            return response;
        }
        catch (OperationCanceledException)
        {
            _requestLog.WriteUnanswered(request, new EngineResponse.Unanswered(ErrorCode.Timeout, "the request is cancelled"));
            throw;
        }

        if (response is EngineResponse.Unanswered or EngineResponse.Busy)
            _requestLog.WriteUnanswered(request, response);
        if (key is not null && keyed is not null)
        {
            // Stored before the answer is shaped for the request, so the store and the published event see
            // the answer's own tree even when the request asked for no stamp.
            _checkRoute.StoreAnswer(key, request, response, keyed);
            response = _checkRoute.ForRequest(request, response);
        }

        return response;
    }

    private async Task<EngineResponse> ScheduledAsync(EngineRequest request, CancellationToken cancellationToken, SchedulerLane? laneOverride = null)
    {
        if (request is EngineRequest.Ping or EngineRequest.ShutDown)
            return new EngineResponse.Acknowledged();
        if (request is EngineRequest.Status)
        {
            try
            {
                return await _inspectionRoute.StatusAsync(request, _initialization, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _inspectionRoute.ScheduleBackground(_shutdown);
            }
        }
        if (request is EngineRequest.YieldMemory)
        {
            try
            {
                return await _inspectionRoute.YieldMemoryAsync(request, WorktreeSnapshot(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _inspectionRoute.ScheduleBackground(_shutdown);
            }
        }
        if (request is EngineRequest.BeginSession or EngineRequest.EndSession or EngineRequest.SessionGuard)
            return await _sessionRoute.SessionAsync(request, cancellationToken).ConfigureAwait(false);
        if (request is EngineRequest.WatchStream watch)
            return await _sessionRoute.WatchFirstAsync(watch, cancellationToken).ConfigureAwait(false);
        if (request is EngineRequest.StoreSliceResult store)
            return _testPlanRoute.StoreSlice(store);

        // The session is credited once, in HandleAsync before the key is taken. A background re-check
        // reaches this call without HandleAsync and credits nothing.

        if (await _inspectionRoute.RefusalBeforeReadyAsync(request, _initialization, cancellationToken).ConfigureAwait(false) is { } refusal)
            return refusal;

        var lane = laneOverride ?? Scheduler.LaneFor(request);
        if (await _inspectionRoute.ShedAsync(lane, _shutdown).ConfigureAwait(false) is { } shed)
            return shed;

        var phases = new PhaseTimes();
        var queued = Stopwatch.StartNew();
        SchedulerLease lease;
        try
        {
            lease = await _scheduler.EnterAsync(lane, RequestLog.KindOf(request), request.WaitMs, cancellationToken).ConfigureAwait(false);
        }
        catch (SchedulerBusyException busy)
        {
            return new EngineResponse.Busy(busy.Position, busy.EstimatedWaitMs);
        }

        var gateHolder = lease.GateHolder;
        phases.Add(Phase.Gate, queued);
        var configurationBefore = _workspace.ConfigurationGeneration;
        var started = Environment.TickCount64;
        var held = true;

        // The wait and the analyzer budget start at dequeue: queue time is the gate phase and counts against neither.
        using var analyzerDeadline = new CancellationTokenSource();
        if (request.WaitMs > 0)
            analyzerDeadline.CancelAfter(TimeSpan.FromMilliseconds(request.WaitMs * AnalyzerRun.ShareOfWait));
        var analyzers = new AnalyzerRun(request is not EngineRequest.CheckFiles { LeaveOutCompilationWide: true }, analyzerDeadline.Token);
        try
        {
            // Each case carries its scope, which becomes the feature's own.
            if (request is EngineRequest.CheckChanges changes)
                return await _checkRoute.CheckChangesAsync(changes, analyzers, phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.CheckPaths paths)
                return await _checkRoute.CheckPathsAsync(paths, analyzers, phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.CheckFiles check)
                return await _checkRoute.CheckFilesAsync(check, analyzers, phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.PlanChangedBuild)
                return await _inspectionRoute.PlanChangedBuildAsync(phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.Graph)
                return await _inspectionRoute.GraphAsync(phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.Impact impact)
                return await _inspectionRoute.ImpactAsync(impact, phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.Judge judge)
                return await _inspectionRoute.JudgeAsync(judge, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.PlanFailedTests failed)
            {
                var pendingFailed = await _testPlanRoute.SelectFailedAsync(failed, phases, cancellationToken).ConfigureAwait(false);
                _inspectionRoute.RememberStatus(configurationBefore, phases);
                held = false;
                lease.Dispose();
                _inspectionRoute.ScheduleBackground(_shutdown);
                return await _testPlanRoute.PrepareAsync(pendingFailed, failed.NoCache, phases, cancellationToken).ConfigureAwait(false);
            }

            var (pending, noCache) = await _testPlanRoute.SelectScopedAsync(request, phases, cancellationToken).ConfigureAwait(false);

            // The shadow preparation reads only the selection, which is immutable, so it runs after a reader is released and a check from another client does not wait behind the emit.
            _inspectionRoute.RememberStatus(configurationBefore, phases);
            held = false;
            lease.Dispose();
            _inspectionRoute.ScheduleBackground(_shutdown);
            // Masked only under --no-cache: under --clean the cache was cleared before the selection, so no run
            // answers from it, but each run keeps its slice key so its verdict refills it.
            return await _testPlanRoute.PrepareAsync(pending, noCache, phases, cancellationToken).ConfigureAwait(false);
        }
        catch (FuseException e)
        {
            return ResponseMapper.Unanswered(e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Write($"{RequestLog.KindOf(request)} failed: {e}");
            return new EngineResponse.Unanswered(ErrorCode.Internal, $"internal error: {e.Message} (details in {_log.FilePath})");
        }
        finally
        {
            // Released whatever the log write does: a lock that stays held makes every later request wait for it.
            try
            {
                if (held)
                    _inspectionRoute.RememberStatus(configurationBefore, phases);
                _requestLog.Write(request, phases, Environment.TickCount64 - started, gateHolder);
            }
            finally
            {
                if (held)
                {
                    lease.Dispose();
                    _inspectionRoute.ScheduleBackground(_shutdown);
                }
            }
        }
    }

    /// <summary>
    ///     Answers <paramref name="request"/> on the worktree it names, or null when this router serves it: a request
    ///     without a worktree root, one that names this router's own root, and the engine-wide requests stay here. A
    ///     root that names no worktree of this repository is answered with <see cref="Fuse.Failures.ErrorCode.InvalidPath"/>.
    /// </summary>
    private async Task<EngineResponse?> RouteToWorktreeAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (request is EngineRequest.Ping or EngineRequest.ShutDown or EngineRequest.YieldMemory)
            return null;
        var named = request.WorktreeRoot;
        if (string.IsNullOrWhiteSpace(named) || string.Equals(named, _root.Path, StringComparison.Ordinal))
            return null;
        var other = RepoRoot.Find(named);
        if (other is null || !string.Equals(other.PipeName, _root.PipeName, StringComparison.Ordinal))
            return new EngineResponse.Unanswered(ErrorCode.InvalidPath, $"\"{named}\" is not a worktree of this repository");
        if (string.Equals(other.StateDirectory, _root.StateDirectory, StringComparison.Ordinal))
            return null;
        return await WorktreeRouter(other).HandleAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The router that serves <paramref name="root"/>, started on its first request and shared by every request for it.</summary>
    private RequestRouter WorktreeRouter(RepoRoot root)
    {
        lock (_worktreeLock)
        {
            if (!_worktreeRouters.TryGetValue(root.StateDirectory, out var child))
            {
                child = new RequestRouter(root, _log, _scheduler, _budget, _watchHub, ownsScheduler: false);
                _worktreeRouters[root.StateDirectory] = child;
                // Started without waiting: the delegated request waits for it the way a primary request waits for this router.
                _ = child.InitializeAsync(_shutdown);
            }

            return child;
        }
    }

    /// <summary>
    ///     Releases the secondary worktree <paramref name="named"/> names, when it names one: removes and disposes its
    ///     child router, which closes its file watcher and its git reader, and unregisters its overlay. A later request
    ///     for it starts a fresh child router again, as on first use. Returns true when a secondary worktree was
    ///     released and the engine keeps serving; false when the shutdown stops the engine: nothing named, the primary
    ///     named, or a path outside this repository.
    /// </summary>
    /// <param name="named">The worktree root the shutdown names, or null when the client named none.</param>
    public bool TryReleaseWorktree(string? named)
    {
        if (string.IsNullOrWhiteSpace(named) || string.Equals(named, _root.Path, StringComparison.Ordinal))
            return false;
        var other = RepoRoot.Find(named);
        if (other is null || !string.Equals(other.PipeName, _root.PipeName, StringComparison.Ordinal))
            return false;
        if (string.Equals(other.StateDirectory, _root.StateDirectory, StringComparison.Ordinal))
            return false;
        RequestRouter? child;
        lock (_worktreeLock)
            _worktreeRouters.Remove(other.StateDirectory, out child);
        // The hypervisor workspace may hold an overlay with no child router above it, so it is unregistered too.
        _workspace.UnregisterWorktree(other);
        child?.Dispose();
        _log.Write(child is null ? $"released worktree {other.Path} (no router served it)" : $"released worktree {other.Path}");
        return true;
    }

    /// <summary>
    ///     Re-checks one live session's files in the background lane, sharing a foreground check of the same
    ///     content key already in flight and storing the answer for the checks that follow it. A Busy or
    ///     unanswered background check stores nothing; the next generation asks again.
    /// </summary>
    private async Task RunBackgroundCheckAsync(BackgroundCheck.SessionFiles session, CancellationToken cancellationToken)
    {
        var request = CheckRoute.BackgroundRequest(session);
        var (key, keyed) = _checkRoute.CoalesceKey(request);
        if (key is null || keyed is null || _answers.TryGet(key, out _))
            return;
        // The scheduler's background token ties the run to its generation too: a newer one cancels both.
        using var generation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _scheduler.BackgroundToken);
        EngineResponse response;
        try
        {
            // The re-check credits nothing: its files are the session's because the writers already say so, and
            // recording the session it answers to would credit a registered session's files to it in the record
            // itself, so its files would still count as that session's after the registration ended or expired
            // and a judge's stop would go on leaving their errors out.
            response = await _scheduler.RunCoalescedAsync(key, token => ScheduledAsync(request, token, SchedulerLane.Background), generation.Token).ConfigureAwait(false);
        }
        catch (SchedulerBusyException busy)
        {
            response = new EngineResponse.Busy(busy.Position, busy.EstimatedWaitMs);
        }
        catch (OperationCanceledException)
        {
            // A newer generation cancelled this run; its run re-checks these files.
            return;
        }

        if (response is EngineResponse.Unanswered or EngineResponse.Busy)
            _requestLog.WriteUnanswered(request, response);
        _checkRoute.StoreAnswer(key, request, response, keyed);
    }

    /// <summary>True while the projects are evaluated and first loaded, or while a background load runs, here or in another worktree this router serves.</summary>
    public bool IsLoading
    {
        get
        {
            if (_initialization is { IsCompleted: false } || _preloader.IsRunning)
                return true;
            lock (_worktreeLock)
                return _worktreeRouters.Values.Any(child => child.IsLoading);
        }
    }

    /// <summary>The planner the router answers test plans with, for a test that answers a plan alongside a check.</summary>
    internal TestPlanner Planner => _planner;

    /// <summary>The gate the first evaluation and the background load hold while they choose work, for a test that holds it while a background choice is made.</summary>
    internal RequestGate RequestLock => _requestLock;

    /// <summary>The scheduler that admits checks and plans to the reader pool, for a test that holds a reader or reads the queue.</summary>
    internal Scheduler Scheduler => _scheduler;

    /// <summary>The result store answers are kept in, for a test that reads a kept answer or a session file.</summary>
    internal AnswerStore Answers => _answers;

    /// <summary>The background re-check of live sessions, for a test that waits for a run to finish.</summary>
    internal BackgroundCheck Background => _background;

    /// <summary>The per-engine memory budget, for a test that reads the ceiling and for the engine's idle watch.</summary>
    internal MemoryBudget Budget => _budget;

    /// <summary>The route that answers what the engine holds, for a worktree child that sheds its caches through it.</summary>
    internal InspectionRoute Inspection => _inspectionRoute;

    /// <summary>The routers serving the other worktrees of this repository, each pinned while it is shed or released.</summary>
    internal List<RequestRouter> WorktreeSnapshot()
    {
        lock (_worktreeLock)
            return [.. _worktreeRouters.Values];
    }

    /// <summary>
    ///     Waits for the background load, warm and re-check to stop, after the shutdown token has been cancelled, so a
    ///     workspace is never disposed under work that is still running. The worktree child routers wait first, bounded,
    ///     so their workspaces are never disposed under a project that is still loading either.
    /// </summary>
    public async Task WaitForPreloadAsync()
    {
        var children = WorktreeSnapshot();
        if (children.Count > 0)
        {
            try
            {
                await Task.WhenAll(children.Select(child => child.WaitForPreloadAsync())).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException)
            {
                _log.Write("shutdown: a worktree preload is still going after 30 s; disposing anyway");
            }
        }

        await Task.WhenAll(_preloader.WaitAsync(), _warmer.WaitAsync(), _background.WaitAsync()).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _background.Dispose();
        if (_ownsScheduler)
            _scheduler.Dispose();
        _workspace.Dispose();
        _requestLock.Dispose();
        lock (_worktreeLock)
        {
            foreach (var child in _worktreeRouters.Values)
                child.Dispose();
            _worktreeRouters.Clear();
        }
    }
}
