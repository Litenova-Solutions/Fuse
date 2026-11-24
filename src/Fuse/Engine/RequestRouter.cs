using System.Collections.Concurrent;
using System.Diagnostics;
using Fuse.Changes;
using Fuse.Changes.Model;
using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Repo;
using Fuse.Telemetry;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Engine;

/// <summary>
///     Owns the warm workspace and answers requests: it initializes the workspace, admits requests through the
///     scheduler (<see cref="Scheduler"/>) to a bounded pool of readers, and routes each to the feature that answers it.
/// </summary>
internal sealed class RequestRouter : IDisposable
{
    private readonly RepoRoot _root;
    private readonly EngineLog _log;
    private readonly RepoWorkspace _workspace;
    private readonly Checker _checker;
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
    private readonly JudgeService _judge;
    private readonly SurfaceGuardStore _guards;
    private readonly WatchHub? _watchHub;

    /// <summary>Guards the last completed sync's status and the distinct touched paths it counted.</summary>
    private readonly Lock _statusLock = new();

    /// <summary>Guards the routers that serve the other worktrees of this repository.</summary>
    private readonly Lock _worktreeLock = new();

    private readonly Dictionary<string, RequestRouter> _worktreeRouters = new(StringComparer.Ordinal);
    private readonly HashSet<RepoPath> _touchedPaths = [];
    private int _touchedConfigurationGeneration;
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
        _checker = new Checker(_workspace, _sessions);
        _planner = new TestPlanner(_workspace);
        _preloader = new Preloader(_workspace, _requestLock, log.Write);
        _warmer = new BaselineWarmer(_workspace, _checker, _requestLock, log.Write);
        _requestLog = new RequestLog(log);
        _scheduler = scheduler;
        _ownsScheduler = ownsScheduler;
        _budget = budget;
        _answers = new AnswerStore(root.StateDirectory, log.Write);
        _background = new BackgroundCheck(LiveSessionFiles, BackgroundKey, _answers, RunBackgroundCheckAsync, log.Write);
        _judge = new JudgeService(_workspace, root);
        _guards = new SurfaceGuardStore(root);
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
                _log.Write($"registered sessions live: {string.Join(", ", live.Select(r => r.Session[SessionEdits.RegisteredPrefix.Length..]).Order(StringComparer.Ordinal))}");
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

            CaptureStatus(reloadMs: null);
        }
        finally
        {
            _requestLock.Exit();
        }

        ScheduleBackground();
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
        NoteSession(request);
        string? key;
        Generation? keyed;
        if (request is EngineRequest.CheckChanges && _initialization is not { IsCompleted: false })
        {
            // A check of every change answers for the files on disk, not for the tree the watcher last
            // reported: a shell write it has not reported yet is in no generation yet, and the kept answer of
            // the tree without it would answer for that tree (and stamp it). The sync confirms the changed
            // set against disk before any kept answer is served, and the key is taken from the generation
            // it publishes. Hooks name their files, which the check itself syncs in by name, so they are
            // not affected by this. Only macOS confirms with git: its file watcher can report a write late,
            // while Windows and Linux report it before another process can ask, and git costs tens of
            // milliseconds on a large repository.
            try
            {
                await _workspace.SyncAndPinAsync([], cancellationToken, confirmWithDisk: OperatingSystem.IsMacOS()).ConfigureAwait(false);
                (key, keyed) = CoalesceKey(request);
            }
            catch (FuseException)
            {
                // The check below surfaces this refusal; no kept answer is served for a tree that could
                // not be confirmed.
                key = null;
                keyed = null;
            }
        }
        else
        {
            // The key is taken once, from the request and the generation it pins: a check of every change
            // takes it after its sync above, from the generation that sync published, and no request reads
            // and hashes every changed file twice before its phase timer starts.
            (key, keyed) = CoalesceKey(request);
        }

        if (key is not null && _answers.TryGet(key, out var stored))
        {
            var phases = new PhaseTimes();
            phases.Add(Phase.Gate, 0);
            _requestLog.Write(request, phases, 0, null);
            return ForRequest(request, new EngineResponse.CheckAnswered(stored));
        }

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
            StoreAnswer(key, request, response, keyed);
            response = ForRequest(request, response);
        }

        return response;
    }

    private async Task<EngineResponse> ScheduledAsync(EngineRequest request, CancellationToken cancellationToken, SchedulerLane? laneOverride = null)
    {
        if (request is EngineRequest.Ping or EngineRequest.ShutDown)
            return new EngineResponse.Acknowledged();
        if (request is EngineRequest.Status)
            return await StatusAsync(request, cancellationToken).ConfigureAwait(false);
        if (request is EngineRequest.YieldMemory)
            return await YieldMemoryAsync(request, cancellationToken).ConfigureAwait(false);
        if (request is EngineRequest.BeginSession or EngineRequest.EndSession or EngineRequest.SessionGuard)
            return await SessionAsync(request, cancellationToken).ConfigureAwait(false);
        if (request is EngineRequest.WatchStream watch)
            return await WatchFirstAsync(watch, cancellationToken).ConfigureAwait(false);
        if (request is EngineRequest.StoreSliceResult store)
            return StoreSlice(store);

        // The session is credited once, in HandleAsync before the key is taken. A background re-check
        // reaches this call without HandleAsync and credits nothing.

        if (await RefusalBeforeReadyAsync(request, cancellationToken).ConfigureAwait(false) is { } refusal)
            return refusal;

        var lane = laneOverride ?? Scheduler.LaneFor(request);
        if (await ShedAsync(lane).ConfigureAwait(false) is { } shed)
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
            {
                var session = SessionEdits.IsValid(changes.Session) ? changes.Session : null;
                var options = new CheckOptions(IncludeRepairBrief: changes.Brief);
                var answered = Stamped(ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.AllChanges(), session, analyzers, phases, options, cancellationToken, maxReported: changes.MaxErrors).ConfigureAwait(false)), stamp: true);
                await PublishGuardViolationsAsync(cancellationToken).ConfigureAwait(false);
                return answered;
            }
            if (request is EngineRequest.CheckPaths paths)
                return await CheckPathsAsync(paths, analyzers, phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.CheckFiles check)
                return await CheckFilesAsync(check, analyzers, phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.PlanChangedBuild)
                return await PlanChangedBuildAsync(phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.Graph)
                return await GraphAsync(phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.Impact impact)
                return await ImpactAsync(impact, phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.Judge judge)
                return await JudgeAsync(judge, cancellationToken).ConfigureAwait(false);

            // MaxFailures is enforced where tests run, not while planning, so the plan carries no trace of it.
            if (request is EngineRequest.PlanFailedTests failed)
            {
                if (failed.Clean)
                    _planner.SliceCache.Clear();
                var (byProject, unknown) = FailedTestProjects(failed);

                var pendingFailed = await _planner.SelectFailedAsync(byProject, unknown, phases, cancellationToken, noCache: failed.NoCache).ConfigureAwait(false);
                RememberStatus(configurationBefore, phases);
                held = false;
                lease.Dispose();
                ScheduleBackground();
                return AnswerPlan(await _planner.PrepareAsync(pendingFailed, phases, cancellationToken).ConfigureAwait(false), failed.NoCache);
            }

            var (cleanCache, noCache) = request switch
            {
                EngineRequest.PlanAffectedTests affected => (affected.Clean, affected.NoCache),
                EngineRequest.PlanAllTests all => (all.Clean, all.NoCache),
                _ => (false, false),
            };
            if (cleanCache)
                _planner.SliceCache.Clear();
            TestScope scope = request switch
            {
                EngineRequest.PlanAffectedTests { Session: { } session } sessioned when SessionEdits.IsValid(session) =>
                    new TestScope.Affected { LeftOut = await OnlyOtherSessionsWroteAsync(session, cancellationToken).ConfigureAwait(false), NamedPaths = sessioned.Projects, BuildOnly = sessioned.BuildOnly, NoCache = sessioned.NoCache },
                EngineRequest.PlanAffectedTests plan => new TestScope.Affected { NamedPaths = plan.Projects, BuildOnly = plan.BuildOnly, NoCache = plan.NoCache },
                EngineRequest.PlanAllTests allTests => new TestScope.All { NoCache = allTests.NoCache },
                _ => throw new UnreachableException($"{request.GetType().Name} is answered before the scheduler"),
            };
            var pending = await _planner.SelectAsync(scope, phases, cancellationToken).ConfigureAwait(false);

            // The shadow preparation reads only the selection, which is immutable, so it runs after a reader is released and a check from another client does not wait behind the emit.
            RememberStatus(configurationBefore, phases);
            held = false;
            lease.Dispose();
            ScheduleBackground();
            // Masked only under --no-cache: under --clean the cache was cleared before the selection, so no run
            // answers from it, but each run keeps its slice key so its verdict refills it.
            return AnswerPlan(await _planner.PrepareAsync(pending, phases, cancellationToken).ConfigureAwait(false), noCache);
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
                    RememberStatus(configurationBefore, phases);
                _requestLog.Write(request, phases, Environment.TickCount64 - started, gateHolder);
            }
            finally
            {
                if (held)
                {
                    lease.Dispose();
                    ScheduleBackground();
                }
            }
        }
    }

    /// <summary>
    ///     Answers the refusal while the workspace is still loading or holds no project, or null when the request may run.
    /// </summary>
    /// <param name="request">The request to refuse while the engine is not ready.</param>
    /// <param name="cancellationToken">The token that stops waiting for initialization.</param>
    private async Task<EngineResponse?> RefusalBeforeReadyAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        var initialization = _initialization ?? Task.CompletedTask;
        if (!initialization.IsCompleted && request is EngineRequest.CheckChanges { WaitForLoad: false } or EngineRequest.CheckFiles { WaitForLoad: false } or EngineRequest.CheckPaths { WaitForLoad: false })
            return new EngineResponse.Unanswered(ErrorCode.Loading, "Fuse is still loading this repository; the next check will include these changes");
        try
        {
            await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (FuseException e)
        {
            return ResponseMapper.Unanswered(e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Write($"initialization failed: {e}");
            return new EngineResponse.Unanswered(ErrorCode.LoadFailed, $"Fuse could not evaluate the repository's projects: {e.Message}");
        }

        if (_workspace.Graph.Projects.Count == 0)
            return new EngineResponse.Unanswered(ErrorCode.NoProjects, _workspace.Graph.Failures.Count > 0
                ? $"no C# project could be evaluated: {_workspace.Graph.Failures[0]}"
                : ErrorMessages.NoProjects);

        return null;
    }

    /// <summary>
    ///     Sheds what the memory budget sheds at this pressure, in its order, and answers Busy past the ceiling with the lane's queue position and estimate; null when the request may run.
    /// </summary>
    /// <param name="lane">The scheduler lane the request waits in.</param>
    private async Task<EngineResponse?> ShedAsync(SchedulerLane lane)
    {
        var budget = _budget.Assess();
        if (budget.Shed.Count > 0)
            _log.Write($"memory: working set {budget.WorkingSetBytes / (1024 * 1024)} MB of {budget.CeilingBytes / (1024 * 1024)} MB, shedding {string.Join(", ", budget.Shed)}");
        if (budget.Shed.Contains(MemoryBudget.ShedStep.KeptAnswersAndCaches))
        {
            _answers.Clear();
            _checker.ClearKeptAnswers();
        }

        if (budget.Shed.Contains(MemoryBudget.ShedStep.SessionViews))
            await _workspace.ClearSessionViewsAsync(_shutdown).ConfigureAwait(false);
        if (budget.Shed.Contains(MemoryBudget.ShedStep.HeadCompilations))
            await _workspace.ClearHeadCompilationsAsync(_shutdown).ConfigureAwait(false);
        if (budget.Shed.Contains(MemoryBudget.ShedStep.IdleConeProjects))
            await _workspace.ClearIdleConeProjectsAsync(_shutdown).ConfigureAwait(false);
        if (budget.IsPastCeiling)
        {
            var (position, estimate) = _scheduler.EstimateFor(lane);
            return new EngineResponse.Busy(Math.Max(1, position), Math.Max(estimate, 1000));
        }

        return null;
    }

    /// <summary>
    ///     The projects a failed-tests plan names, each with the tests that failed in it, and the names that are not a path.
    /// </summary>
    /// <param name="failed">The request for a plan of the tests that failed last time.</param>
    private (Dictionary<RepoPath, string[]> ByProject, List<string> Unknown) FailedTestProjects(EngineRequest.PlanFailedTests failed)
    {
        var byProject = new Dictionary<RepoPath, string[]>();
        var unknown = new List<string>();
        foreach (var project in failed.Projects)
        {
            if (string.IsNullOrWhiteSpace(project.Project))
                continue;
            try
            {
                byProject[_workspace.Root.PathOfNamed(project.Project)] = project.Tests ?? [];
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException)
            {
                unknown.Add(project.Project);
            }
        }

        return (byProject, unknown);
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
    ///     Credits the session a request carries with the files it names and marks a stopping session stopped.
    ///     Recorded before anything can refuse the check, so a check answered "still loading" still credits the
    ///     session with its files, and before a kept answer is served, so its record is the one that key held.
    /// </summary>
    private void NoteSession(EngineRequest request)
    {
        if (request is EngineRequest.CheckFiles { Session: { } writer } written && SessionEdits.IsValid(writer))
            RecordSession(writer, written.Files);
        if (request is EngineRequest.CheckChanges { IsStopping: true, Session: { } stopping } && SessionEdits.IsValid(stopping))
            _sessions.MarkStopped(stopping);
    }

    /// <summary>
    ///     A kept or shared answer as <paramref name="request"/> asked for it: with the tree the check stamped it
    ///     with when the request asked for a stamp, and without one when it did not. A kept answer carries no tree,
    ///     so one that asked for a stamp is stamped from the newest generation, whose content matches it by its key.
    ///     The errors a stamp names are the same either way.
    /// </summary>
    private EngineResponse ForRequest(EngineRequest request, EngineResponse response)
    {
        if (response is not EngineResponse.CheckAnswered answered)
            return response;
        var stamp = request switch
        {
            EngineRequest.CheckChanges changes => changes.StampTree,
            EngineRequest.CheckFiles files => files.StampTree,
            EngineRequest.CheckPaths paths => paths.StampTree,
            _ => false,
        };
        if (stamp)
            return answered.Report.Tree is null ? Stamped(response, stamp: true) : response;
        return answered.Report.Tree is null
            ? response
            : new EngineResponse.CheckAnswered(answered.Report with { Tree = null, TreeFiles = 0 });
    }

    /// <summary>Keeps a check answer by its content key, as the session file of the session it was answered to, and publishes the session's answer when it changed.</summary>
    /// <param name="keyed">The generation the answer's key was taken from: the check answers from it or from a newer one it synced itself, so the kept and published generation never runs ahead of the errors.</param>
    private void StoreAnswer(string key, EngineRequest request, EngineResponse response, Generation keyed)
    {
        if (response is not EngineResponse.CheckAnswered answered)
            return;
        var tree = answered.Report.Tree ?? TreeStamp.Of(ChangedPairs(keyed.Changed));
        var session = RequestLog.SessionOf(request);
        CheckReport? previous = null;
        if (session is not null && _answers.TryGetSession(session, out var kept))
            previous = kept;
        _answers.Keep(key, session, keyed.Number, tree, answered.Report);
        if (session is not null && _watchHub is not null && AnswerChanged(previous, answered.Report))
            _watchHub.Publish(new WatchEvent("check", session, _root.Path, keyed.Number, tree, answered.Report.ErrorCount, answered.Report.Errors));
    }

    /// <summary>True when <paramref name="report"/> answers differently than <paramref name="previous"/>: a first answer, or different errors.</summary>
    private static bool AnswerChanged(CheckReport? previous, CheckReport report)
    {
        if (previous is null)
            return true;
        if (previous.ErrorCount != report.ErrorCount || previous.Errors.Length != report.Errors.Length)
            return true;
        for (var i = 0; i < previous.Errors.Length; i++)
        {
            if (previous.Errors[i].Error.ToString() != report.Errors[i].Error.ToString())
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Publishes a watch event for every frozen declaration the tree changed: after each check, so a watcher learns
    ///     of a violation with the answer that found the edit. Never fails the check it follows.
    /// </summary>
    private async Task PublishGuardViolationsAsync(CancellationToken cancellationToken)
    {
        if (_watchHub is null)
            return;
        try
        {
            var generation = _workspace.PinGeneration();
            if (generation.Changed.Count == 0)
                return;
            var declarations = await ChangedDeclarationsAsync(generation, cancellationToken).ConfigureAwait(false);
            foreach (var violation in _guards.Violations(generation.Changed, declarations))
                _watchHub.Publish(violation);
        }
        catch (Exception e)
        {
            // A cancelled client disconnects; anything else is logged. Either way the check it follows still answers.
            if (e is not OperationCanceledException)
                _log.Write($"guard violations skipped: {e.Message}");
        }
    }

    /// <summary>
    ///     The changed declaration headers of each changed C# file in <paramref name="generation"/>: the working-tree
    ///     header of an added or changed declaration, the HEAD header of a removed one, across every project that
    ///     compiles the file. A file that cannot be read reports no declarations.
    /// </summary>
    private async Task<Dictionary<RepoPath, IReadOnlyList<string>>> ChangedDeclarationsAsync(Generation generation, CancellationToken cancellationToken)
    {
        var declarations = new ConcurrentDictionary<RepoPath, IReadOnlyList<string>>();
        await Parallel.ForEachAsync(
            generation.Changed.Where(path => path.Absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken },
            async (path, ct) =>
            {
                try
                {
                    if (await DeclarationsOfAsync(path, generation, ct).ConfigureAwait(false) is { Count: > 0 } headers)
                        declarations[path] = headers;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.Write($"guard violations skipped for {path.Relative}: {e.Message}");
                }
            }).ConfigureAwait(false);
        return new Dictionary<RepoPath, IReadOnlyList<string>>(declarations);
    }

    /// <summary>The changed declaration headers of <paramref name="path"/>, or null when its surface reads the same as HEAD.</summary>
    private async Task<IReadOnlyList<string>?> DeclarationsOfAsync(RepoPath path, Generation generation, CancellationToken cancellationToken)
    {
        var headers = new HashSet<string>(StringComparer.Ordinal);
        var baseline = generation.Baseline;
        var current = generation.Current;
        var projectIds = baseline.GetDocumentIdsWithFilePath(path.Absolute).Concat(current.GetDocumentIdsWithFilePath(path.Absolute)).Select(id => id.ProjectId).Distinct().ToList();
        if (projectIds.Count == 0)
        {
            var before = _workspace.HeadText(path) is { } head ? FileDeclarations.Of(await ParseAsync(head, cancellationToken).ConfigureAwait(false)) : FileDeclarations.Empty;
            var after = await DiskDeclarationsAsync(path, cancellationToken).ConfigureAwait(false);
            AddHeaders(headers, SurfaceDiff.Compare(before, after).Changes);
        }
        else
        {
            foreach (var projectId in projectIds)
            {
                var before = await DeclarationsInAsync(baseline, projectId, path, cancellationToken).ConfigureAwait(false);
                var after = await DeclarationsInAsync(current, projectId, path, cancellationToken).ConfigureAwait(false);
                AddHeaders(headers, SurfaceDiff.Compare(before, after).Changes);
            }
        }

        return headers.Count == 0 ? null : [.. headers.Order(StringComparer.Ordinal)];
    }

    /// <summary>The declarations <paramref name="solution"/> holds for <paramref name="path"/> in <paramref name="projectId"/>, or none when it holds no document there.</summary>
    private static async Task<FileDeclarations> DeclarationsInAsync(Solution solution, ProjectId projectId, RepoPath path, CancellationToken cancellationToken)
    {
        var document = solution.GetDocumentIdsWithFilePath(path.Absolute).Where(id => id.ProjectId == projectId).Select(solution.GetDocument).FirstOrDefault(d => d is not null);
        return document is not null && await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is { } root ? FileDeclarations.Of(root) : FileDeclarations.Empty;
    }

    /// <summary>The declarations of <paramref name="path"/> as the working tree holds it, or none when it is gone.</summary>
    private static async Task<FileDeclarations> DiskDeclarationsAsync(RepoPath path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path.Absolute))
            return FileDeclarations.Empty;
        var text = SolutionViews.Decode(SharedRead.AllBytes(path.Absolute));
        return FileDeclarations.Of(await ParseAsync(text, cancellationToken).ConfigureAwait(false));
    }

    private static Task<SyntaxNode> ParseAsync(SourceText text, CancellationToken cancellationToken) =>
        CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview), cancellationToken: cancellationToken)
            .GetRootAsync(cancellationToken);

    /// <summary>Adds the header of each change: the working-tree header of an added or changed declaration, the HEAD header of a removed one.</summary>
    private static void AddHeaders(HashSet<string> headers, IReadOnlyList<DeclarationChange> changes)
    {
        foreach (var change in changes)
        {
            headers.Add(change switch
            {
                DeclarationChange.Added added => added.After,
                DeclarationChange.Changed edited => edited.After,
                DeclarationChange.Removed removed => removed.Before,
                _ => throw new UnreachableException($"a declaration change is added, changed or removed, not {change.GetType().Name}"),
            });
        }
    }

    /// <summary>The request a background re-check of <paramref name="session"/> sends: its files, answered to it, with every analyzer and a tree stamp.</summary>
    private static EngineRequest.CheckFiles BackgroundRequest(BackgroundCheck.SessionFiles session) =>
        new EngineRequest.CheckFiles([.. session.Files.Select(f => f.Absolute)], WaitForLoad: true) { Session = session.Session, StampTree = true };

    /// <summary>The content key a background re-check of <paramref name="session"/> coalesces and is stored by.</summary>
    private string? BackgroundKey(BackgroundCheck.SessionFiles session) => CoalesceKey(BackgroundRequest(session)).Key;

    /// <summary>
    ///     The live sessions of the newest generation and the files each wrote in it, from its writers without a
    ///     sync: a session with no changed file in it is not re-checked. The generation may predate a credit a
    ///     kept answer recorded, so this read drops no credit; only a sync that verifies a file matches HEAD does.
    /// </summary>
    private IReadOnlyList<BackgroundCheck.SessionFiles> LiveSessionFiles()
    {
        var generation = _workspace.PinGeneration();
        var bySession = new Dictionary<string, List<RepoPath>>(StringComparer.Ordinal);
        foreach (var (path, sessions) in _sessions.WritersOf(generation.Changed))
        {
            foreach (var session in sessions)
            {
                if (!bySession.TryGetValue(session, out var files))
                    bySession[session] = files = [];
                files.Add(path);
            }
        }

        return [.. bySession.OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new BackgroundCheck.SessionFiles(s.Key, generation.Number, [.. s.Value.OrderBy(f => f.Relative, StringComparer.Ordinal)]))];
    }

    /// <summary>
    ///     Re-checks one live session's files in the background lane, sharing a foreground check of the same
    ///     content key already in flight and storing the answer for the checks that follow it. A Busy or
    ///     unanswered background check stores nothing; the next generation asks again.
    /// </summary>
    private async Task RunBackgroundCheckAsync(BackgroundCheck.SessionFiles session, CancellationToken cancellationToken)
    {
        var request = BackgroundRequest(session);
        var (key, keyed) = CoalesceKey(request);
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
        StoreAnswer(key, request, response, keyed);
    }

    /// <summary>Records that <paramref name="session"/> wrote <paramref name="files"/>. A name that is not a valid path is left to the check to refuse.</summary>
    private void RecordSession(string session, IReadOnlyList<string> files)
    {
        var paths = new List<RepoPath>(files.Count);
        foreach (var file in files)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(file))
                    paths.Add(_workspace.Root.PathOfNamed(file));
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException)
            {
                // CheckFilesAsync answers this file with InvalidPath.
            }
        }

        _sessions.Record(session, paths);
        // The next sync takes these files whoever runs it, so a credit is never forgotten as if its file matched HEAD
        // because an answer was served from the result store before the watcher reported the write.
        _workspace.Tracker.ReportWritten(paths);
    }

    /// <summary>
    ///     The changed files whose writers are all other sessions than <paramref name="session"/>, after a sync, so a plan
    ///     for the session leaves their changes out. A file no session is credited with, or one the session wrote, stays.
    /// </summary>
    private async Task<IReadOnlySet<RepoPath>> OnlyOtherSessionsWroteAsync(string session, CancellationToken cancellationToken)
    {
        var generation = await _workspace.SyncAndPinAsync([], cancellationToken).ConfigureAwait(false);
        // Scoping lives in SessionEdits, so a plan and a stop check leave out the same files.
        return _sessions.OnlyOtherSessionsWrote(generation.Changed, session);
    }

    /// <summary>
    ///     Lets the background load, warm and re-check continue after the newest generation, unless the memory
    ///     budget sheds background work at this boundary, which stops the re-check that is running too.
    /// </summary>
    private void ScheduleBackground()
    {
        if (_budget.Assess().Shed.Contains(MemoryBudget.ShedStep.Background))
        {
            _background.CancelCurrent();
            _log.Write("memory: background load and warm shed at this request boundary");
            return;
        }

        _preloader.Schedule(_shutdown);
        _warmer.Schedule(_shutdown);
        try
        {
            _background.Schedule(_workspace.PinGeneration().Number, _shutdown);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // The workspace is going away with the router; no generation is left to re-check.
        }
    }

    /// <summary>
    ///     The content key a check coalesces by with the generation it was taken from, or nulls when it does not
    ///     coalesce: its scope, session, analyzer mode and the generation content hash the kept answer keys by. A
    ///     tree past the kept-answer bound, or a changed file that cannot be read, coalesces nothing.
    /// </summary>
    private (string? Key, Generation? Pinned) CoalesceKey(EngineRequest request)
    {
        string scopeKey;
        string? session;
        bool compilationWide;
        int maxErrors;
        switch (request)
        {
            case EngineRequest.CheckChanges changes:
                scopeKey = "all" + (changes.Brief ? "|brief" : "");
                session = SessionEdits.IsValid(changes.Session) ? changes.Session : null;
                compilationWide = true;
                maxErrors = changes.MaxErrors;
                break;
            case EngineRequest.CheckFiles files:
                scopeKey = "files:" + string.Join(",", files.Files.Order(StringComparer.Ordinal)) + (files.Brief ? "|brief" : "") + (files.InteractiveFast ? "|fast" : "");
                session = SessionEdits.IsValid(files.Session) ? files.Session : null;
                compilationWide = !files.LeaveOutCompilationWide;
                maxErrors = files.MaxErrors;
                break;
            case EngineRequest.CheckPaths paths:
                scopeKey = "paths:" + string.Join(",", paths.Paths.Order(StringComparer.Ordinal)) + (paths.Brief ? "|brief" : "");
                session = SessionEdits.IsValid(paths.Session) ? paths.Session : null;
                compilationWide = true;
                maxErrors = paths.MaxErrors;
                break;
            default:
                return (null, null);
        }

        Generation generation;
        try
        {
            generation = _workspace.PinGeneration();
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            return (null, null);
        }

        if (generation.Changed.Count > KeptCheck.MaxChangedFiles)
            return (null, null);
        var kept = KeptCheck.KeyFor(
            scopeKey,
            maxErrors,
            session,
            compilationWide,
            generation.Head ?? "",
            generation.ConfigurationGeneration,
            generation.BaselineGeneration,
            _sessions.RecordsHash(),
            generation.Changed.Select(path => (path.Relative, path.Absolute)));
        // The stamp a request asks for is no part of the key: it names the same errors, and each waiter of a
        // shared check is stamped, or not, for itself when it is answered.
        return kept is null ? (null, null) : ($"{scopeKey}|{session ?? ""}|{compilationWide}|{kept}", generation);
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

    /// <summary>
    ///     Stores the status of the sync that just completed, counted the way the code counts each threshold: changed
    ///     sources a project owns, and distinct touched paths against the limit past which projects re-evaluate. The
    ///     distinct count is the router's count of the paths each completed sync held; the views hold the same paths.
    /// </summary>
    /// <param name="reloadMs">How long that sync's reload took, or null when it patched without reloading.</param>
    private EngineStatus CaptureStatus(long? reloadMs)
    {
        var generation = _workspace.PinGeneration();
        var changed = generation.Changed;
        lock (_statusLock)
        {
            if (generation.ConfigurationGeneration != _touchedConfigurationGeneration)
            {
                // The touched set resets only when it passed its limit and the next sync re-evaluated; a reload that
                // did not pass it keeps the paths it already held.
                if (_touchedPaths.Count > SolutionViews.MaxTouchedPaths)
                    _touchedPaths.Clear();
                _touchedConfigurationGeneration = generation.ConfigurationGeneration;
            }

            _touchedPaths.UnionWith(changed);
            var status = new EngineStatus(
                Environment.ProcessId,
                Environment.WorkingSet,
                changed.Count,
                generation.Head,
                TreeStamp.Of(ChangedPairs(changed)))
            {
                ChangedSourcesInReach = changed.Count(path => generation.Graph.OwnersOf(path).Count > 0),
                GateHolder = _scheduler.CurrentHolder,
                MemoryCeilingBytes = _budget.CeilingBytes,
                MemoryLoadBytes = _budget.Assess().MemoryLoadBytes,
                TouchedCount = _touchedPaths.Count,
                ReloadMs = reloadMs,
            };
            return status;
        }
    }

    /// <summary>Stores the status after a sync, naming its reload when the configuration generation moved during it.</summary>
    private void RememberStatus(int configurationBefore, PhaseTimes phases)
    {
        long? reloadMs = null;
        if (_workspace.ConfigurationGeneration != configurationBefore)
        {
            var sync = phases.All.FirstOrDefault(phase => phase.Phase == Phase.Sync);
            reloadMs = sync.Phase is null ? 0 : (long)Math.Round(sync.Ms);
        }

        CaptureStatus(reloadMs);
        _scheduler.PublishGeneration(_workspace.PinGeneration().Number);
    }

    /// <summary>
    ///     Checks the files <paramref name="check"/> names. The wire carries them as strings, absolute or relative to the
    ///     root, in whatever letter case the client wrote; each becomes a <see cref="RepoPath"/> in the spelling the file
    ///     system holds it under, and one that is empty or not a valid path is answered with
    ///     <see cref="Fuse.Failures.ErrorCode.InvalidPath"/>, naming it, before anything is checked.
    /// </summary>
    private async Task<EngineResponse> CheckFilesAsync(EngineRequest.CheckFiles check, AnalyzerRun analyzers, PhaseTimes phases, CancellationToken cancellationToken)
    {
        var paths = new List<RepoPath>(check.Files.Count);
        foreach (var file in check.Files)
        {
            if (string.IsNullOrWhiteSpace(file))
                return new EngineResponse.Unanswered(ErrorCode.InvalidPath, ErrorMessages.EmptyPath);
            try
            {
                paths.Add(_workspace.Root.PathOfNamed(file));
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException)
            {
                return new EngineResponse.Unanswered(ErrorCode.InvalidPath, ErrorMessages.InvalidPath(file));
            }
        }

        var session = SessionEdits.IsValid(check.Session) ? check.Session : null;
        var options = new CheckOptions(check.InteractiveFast, check.Brief);
        var answered = Stamped(ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.Files(paths), session, analyzers, phases, options, cancellationToken, maxReported: check.MaxErrors).ConfigureAwait(false)), stamp: true);
        await PublishGuardViolationsAsync(cancellationToken).ConfigureAwait(false);
        return answered;
    }

    /// <summary>
    ///     Answers what the tree holds, bypassing the scheduler entirely: it syncs under the workspace writer and
    ///     reads the newest generation the sync publishes, naming the reader that runs meanwhile when one does; it
    ///     never waits behind a queued check, and never loads a project and never refuses a repository without one.
    ///     Its status carries the engine's working set, its memory ceiling and its GC memory load.
    /// </summary>
    private async Task<EngineResponse> StatusAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        var initialization = _initialization ?? Task.CompletedTask;
        if (!initialization.IsCompleted)
        {
            try
            {
                await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (FuseException e)
            {
                return ResponseMapper.Unanswered(e);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Write($"initialization failed: {e}");
                return new EngineResponse.Unanswered(ErrorCode.LoadFailed, $"Fuse could not evaluate the repository's projects: {e.Message}");
            }

        }

        var phases = new PhaseTimes();
        phases.Add(Phase.Gate, 0);
        var gateHolder = _scheduler.CurrentHolder;
        var configurationBefore = _workspace.ConfigurationGeneration;
        var started = Environment.TickCount64;
        try
        {
            var syncing = phases.Start();
            await _workspace.SyncAsync([], cancellationToken).ConfigureAwait(false);
            phases.Add(Phase.Sync, syncing);
            long? reloadMs = _workspace.ConfigurationGeneration != configurationBefore
                ? (long)Math.Round(syncing?.Elapsed.TotalMilliseconds ?? 0)
                : null;
            return new EngineResponse.StatusAnswered(CaptureStatus(reloadMs));
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
                _requestLog.Write(request, phases, Environment.TickCount64 - started, gateHolder);
            }
            finally
            {
                ScheduleBackground();
            }
        }
    }

    /// <summary>
    ///     Plans a build of the changed projects: it syncs like a check, then plans from the
    ///     graph and the changed files. It never loads a project.
    /// </summary>
    private async Task<EngineResponse> PlanChangedBuildAsync(PhaseTimes phases, CancellationToken cancellationToken)
    {
        var syncing = phases.Start();
        var generation = await _workspace.SyncAndPinAsync([], cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        var projectFiles = (await GitStatus.ChangedPathsAsync(_workspace.Root, cancellationToken).ConfigureAwait(false))
            .Where(path => PathRules.IsProjectFile(path.Absolute))
            .ToList();
        return new EngineResponse.ChangedBuildAnswered(ChangedBuildPlanner.Plan(generation.Graph, generation.Changed, projectFiles));
    }

    /// <summary>Answers the project graph, coupling clusters and test ownership from the generation a sync pins.</summary>
    private async Task<EngineResponse> GraphAsync(PhaseTimes phases, CancellationToken cancellationToken)
    {
        var syncing = phases.Start();
        var generation = await _workspace.SyncAndPinAsync([], cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        return new EngineResponse.GraphAnswered(GraphReportBuilder.Build(generation));
    }

    /// <summary>
    ///     Answers the projects, files and tests the named paths reach. A path that is empty or not a valid path is
    ///     answered with <see cref="Fuse.Failures.ErrorCode.InvalidPath"/>, naming it, before anything is read.
    /// </summary>
    private async Task<EngineResponse> ImpactAsync(EngineRequest.Impact impact, PhaseTimes phases, CancellationToken cancellationToken)
    {
        foreach (var spec in impact.Paths)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return new EngineResponse.Unanswered(ErrorCode.InvalidPath, ErrorMessages.EmptyPath);
            try
            {
                _workspace.Root.PathOfNamed(spec);
            }
            catch (Exception e) when (e is ArgumentException or PathTooLongException)
            {
                return new EngineResponse.Unanswered(ErrorCode.InvalidPath, ErrorMessages.InvalidPath(spec));
            }
        }

        var syncing = phases.Start();
        var generation = await _workspace.SyncAndPinAsync([], cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        var selecting = phases.Start();
        var report = await ImpactReportBuilder.BuildAsync(_workspace, generation, impact.Paths, cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Selection, selecting);
        return new EngineResponse.ImpactAnswered(report);
    }

    /// <summary>Answers a judge request: the verdicts of its candidate patches, ranked.</summary>
    private async Task<EngineResponse> JudgeAsync(EngineRequest.Judge judge, CancellationToken cancellationToken) =>
        new EngineResponse.JudgeAnswered(await _judge.RunAsync(judge.Spec, cancellationToken).ConfigureAwait(false));

    /// <summary>
    ///     Sheds the transient caches of every workspace this engine serves and compacts the heap, answering how many
    ///     bytes of working set that reclaimed and what it is after. It holds no reader: shedding is the answer to
    ///     memory pressure, never another waiter for it.
    /// </summary>
    private async Task<EngineResponse> YieldMemoryAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        var phases = new PhaseTimes();
        phases.Add(Phase.Gate, 0);
        var started = Environment.TickCount64;
        try
        {
            var before = Environment.WorkingSet;
            await YieldCachesAsync(cancellationToken).ConfigureAwait(false);
            _workspace.YieldMemory();
            var after = Environment.WorkingSet;
            return new EngineResponse.MemoryYielded(Math.Max(0, before - after), after);
        }
        finally
        {
            try
            {
                _requestLog.Write(request, phases, Environment.TickCount64 - started, _scheduler.CurrentHolder);
            }
            finally
            {
                ScheduleBackground();
            }
        }
    }

    /// <summary>Sheds this router's workspace caches and every worktree router's below it, without collecting: the yield collects once after this returns.</summary>
    private async Task YieldCachesAsync(CancellationToken cancellationToken)
    {
        List<RequestRouter> children;
        lock (_worktreeLock)
            children = [.. _worktreeRouters.Values];
        foreach (var child in children)
            await child.YieldCachesAsync(cancellationToken).ConfigureAwait(false);
        await _workspace.ClearSessionViewsAsync(cancellationToken).ConfigureAwait(false);
        await _workspace.ClearHeadCompilationsAsync(cancellationToken).ConfigureAwait(false);
        await _workspace.ClearIdleConeProjectsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var overlay in _workspace.Worktrees)
            overlay.TrimTransientCaches(clearSessionViews: true, dropCompiledBaseline: true);
    }

    /// <summary>
    ///     Answers a watch subscription with its first event. The engine server streams the rest without calling here:
    ///     it subscribes to the watch hub itself and writes one line per event until the client disconnects.
    /// </summary>
    private async Task<EngineResponse> WatchFirstAsync(EngineRequest.WatchStream watch, CancellationToken cancellationToken)
    {
        if (_watchHub is null)
            return new EngineResponse.WatchEventPushed(new WatchEvent("heartbeat", watch.Session, _root.Path, Message: "watching"));
        await foreach (var watchEvent in _watchHub.SubscribeAsync(watch.Session, watch.All, cancellationToken).ConfigureAwait(false))
            return new EngineResponse.WatchEventPushed(watchEvent);
        throw new UnreachableException("a watch subscription ends only when cancelled");
    }

    /// <summary>
    ///     The check answer with its own tree stamp, from the changed set the check just synced, so the client does
    ///     not ask git again and the store and the published event name the tree the errors were found on. Every
    ///     fresh answer is stamped; the answer is shaped for the request afterwards, which strips the stamp when the
    ///     request asked for none. Unchanged when stamp is false or the check was not answered.
    /// </summary>
    private EngineResponse Stamped(EngineResponse response, bool stamp)
    {
        if (!stamp || response is not EngineResponse.CheckAnswered answered)
            return response;
        var generation = _workspace.PinGeneration();
        var changed = generation.Changed;
        return new EngineResponse.CheckAnswered(answered.Report with { Tree = TreeStamp.Of(ChangedPairs(changed)), TreeFiles = changed.Count });
    }

    /// <summary>What the tree stamp hashes: each changed file with its path relative to the root and its absolute path to read.</summary>
    private static IEnumerable<(string Relative, string Absolute)> ChangedPairs(IReadOnlyCollection<RepoPath> changed) =>
        changed.Select(path => (path.Relative, path.Absolute));

    /// <summary>Registers the session that <paramref name="begin"/> names.</summary>
    private EngineResponse BeginSession(EngineRequest.BeginSession begin)
    {
        try
        {
            _sessions.BeginRegistration(begin.Name, begin.Paths, begin.Pid);
            return new EngineResponse.Acknowledged();
        }
        catch (FuseException e)
        {
            return ResponseMapper.Unanswered(e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Write($"BeginSession failed: {e}");
            return new EngineResponse.Unanswered(ErrorCode.Internal, $"internal error: {e.Message} (details in {_log.FilePath})");
        }
    }

    /// <summary>Ends the registration that <paramref name="end"/> names.</summary>
    private EngineResponse EndSession(EngineRequest.EndSession end)
    {
        try
        {
            return _sessions.EndRegistration(end.Name)
                ? new EngineResponse.Acknowledged()
                : new EngineResponse.Unanswered(ErrorCode.NoRegisteredSession, ErrorMessages.NoRegisteredSession(end.Name));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Write($"EndSession failed: {e}");
            return new EngineResponse.Unanswered(ErrorCode.Internal, $"internal error: {e.Message} (details in {_log.FilePath})");
        }
    }

    /// <summary>Applies the frozen patterns that <paramref name="guard"/> registers or removes.</summary>
    private EngineResponse GuardSession(EngineRequest.SessionGuard guard)
    {
        try
        {
            _guards.Apply(guard.Guard);
            return new EngineResponse.Acknowledged();
        }
        catch (FuseException e)
        {
            return ResponseMapper.Unanswered(e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Write($"SessionGuard failed: {e}");
            return new EngineResponse.Unanswered(ErrorCode.Internal, $"internal error: {e.Message} (details in {_log.FilePath})");
        }
    }

    /// <summary>
    ///     Answers a session registration or a session guard without the scheduler, at once: <see cref="SessionEdits"/>
    ///     applies a registration under its own lock, so a check that reads its writers under that lock sees the
    ///     registration whole or not at all. It waits for no initialization and holds no reader; neither needs the workspace.
    ///     A check answered after a registration counts its files as that session's from that answer on.
    /// </summary>
    private Task<EngineResponse> SessionAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(request switch
        {
            EngineRequest.BeginSession begin => BeginSession(begin),
            EngineRequest.EndSession end => EndSession(end),
            EngineRequest.SessionGuard guard => GuardSession(guard),
            _ => throw new UnreachableException($"{request.GetType().Name} is not a session request"),
        });
    }

    /// <summary>The answer to a test plan request.</summary>
    /// <param name="noCache">True when the plan skipped the slice cache, so no run answers from it.</param>
    private static EngineResponse.PlanAnswered AnswerPlan(TestPlanResult result, bool noCache = false) => new EngineResponse.PlanAnswered(ResponseMapper.Plan(result, noCache));

    /// <summary>
    ///     Stores one finished test slice's verdict in the slice cache, without the scheduler and at once: the cache
    ///     applies a store under its own locks, so a plan that reads it sees the verdict whole or not at all. It
    ///     waits for no initialization and holds no reader; a store needs only the shared state directory. A line
    ///     with no slice key is ignored, and the answer is always acknowledged, because a store never fails a run.
    /// </summary>
    private EngineResponse.Acknowledged StoreSlice(EngineRequest.StoreSliceResult store)
    {
        if (!string.IsNullOrWhiteSpace(store.SliceKey))
        {
            _planner.SliceCache.Store(new SliceCacheResult(
                store.SliceKey,
                store.Project,
                store.ProjectName,
                "",
                store.Passed,
                store.Failed,
                store.Skipped,
                store.DurationMs,
                DateTime.UtcNow,
                [.. store.Failures.Select(f => new SliceTestFailure(f.Name, f.Message, f.Frames))]));
            _log.Write($"slice cache: stored {store.SliceKey} ({store.Passed} passed, {store.Failed} failed, {store.Skipped} skipped)");
        }

        return new EngineResponse.Acknowledged();
    }

    /// <summary>
    ///     Checks the paths <paramref name="check"/> names. A path that is empty is answered with
    ///     <see cref="Fuse.Failures.ErrorCode.InvalidPath"/> before anything is checked; the checker expands the rest.
    /// </summary>
    private async Task<EngineResponse> CheckPathsAsync(EngineRequest.CheckPaths check, AnalyzerRun analyzers, PhaseTimes phases, CancellationToken cancellationToken)
    {
        foreach (var spec in check.Paths)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return new EngineResponse.Unanswered(ErrorCode.InvalidPath, ErrorMessages.EmptyPath);
        }

        var options = new CheckOptions(IncludeRepairBrief: check.Brief);
        var answered = Stamped(ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.NamedPaths(check.Paths), SessionEdits.IsValid(check.Session) ? check.Session : null, analyzers, phases, options, cancellationToken, maxReported: check.MaxErrors).ConfigureAwait(false)), stamp: true);
        await PublishGuardViolationsAsync(cancellationToken).ConfigureAwait(false);
        return answered;
    }

    /// <summary>
    ///     Waits for the background load, warm and re-check to stop, after the shutdown token has been cancelled, so a
    ///     workspace is never disposed under work that is still running. The worktree child routers wait first, bounded,
    ///     so their workspaces are never disposed under a project that is still loading either.
    /// </summary>
    public async Task WaitForPreloadAsync()
    {
        List<RequestRouter> children;
        lock (_worktreeLock)
            children = [.. _worktreeRouters.Values];
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
