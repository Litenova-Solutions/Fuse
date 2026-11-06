using System.Diagnostics;
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

namespace Fuse.Engine;

/// <summary>
///     Owns the warm workspace and answers requests: it initializes the workspace, admits requests through the
///     scheduler (<see cref="Scheduler"/>) to a bounded pool of readers, and routes each to the feature that answers it.
/// </summary>
internal sealed class RequestRouter : IDisposable
{
    private readonly EngineLog _log;
    private readonly RepoWorkspace _workspace;
    private readonly Checker _checker;
    private readonly TestPlanner _planner;
    private readonly RequestGate _requestLock = new();
    private readonly Scheduler _scheduler;
    private readonly MemoryBudget _budget;
    private readonly Preloader _preloader;
    private readonly BaselineWarmer _warmer;
    private readonly RequestLog _requestLog;
    private readonly SessionEdits _sessions;
    private readonly ResultStore _results;
    private readonly BackgroundCheck _background;

    /// <summary>Guards the last completed sync's status and the distinct touched paths it counted.</summary>
    private readonly Lock _statusLock = new();

    private readonly HashSet<RepoPath> _touchedPaths = [];
    private int _touchedConfigurationGeneration;
    private Task? _initialization;
    private CancellationToken _shutdown;

    public RequestRouter(RepoRoot root, EngineLog log)
        : this(root, log, new Scheduler(Scheduler.DefaultReaderCount), new MemoryBudget())
    {
    }

    internal RequestRouter(RepoRoot root, EngineLog log, Scheduler scheduler, MemoryBudget budget)
    {
        _log = log;
        _workspace = new RepoWorkspace(root, log.Write);
        _sessions = new SessionEdits(root, Path.Combine(root.StateDirectory, "sessions.tsv"), Path.Combine(root.StateDirectory, "registered-sessions.tsv"), log: log.Write);
        _checker = new Checker(_workspace, _sessions);
        _planner = new TestPlanner(_workspace);
        _preloader = new Preloader(_workspace, _requestLock, log.Write);
        _warmer = new BaselineWarmer(_workspace, _checker, _requestLock, log.Write);
        _requestLog = new RequestLog(log);
        _scheduler = scheduler;
        _budget = budget;
        _results = new ResultStore(root.StateDirectory, log.Write);
        _background = new BackgroundCheck(LiveSessionFiles, BackgroundKey, _results, RunBackgroundCheckAsync, log.Write);
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
    ///     and reads the newest generation; a session registration bypasses it too, under its own lock. A check or
    ///     a test plan waits for initialization (a check that does not wait for the load is refused while loading),
    ///     then for a reader in its lane, then runs. A check whose content key matches one in flight shares it;
    ///     one whose key the result store already keeps is answered from it, stamped or not as it asked. Every
    ///     check answer is kept by its key and, for a session, as its session file, and every Unanswered and
    ///     Busy answer is logged with its kind, its code and its session. A request that will not start before its wait ends, and one past the memory ceiling, is answered Busy
    ///     at once. A check's wait and analyzer budget start when a reader takes it, not when it queued. A check
    ///     holds a reader until it is answered; a test plan holds one through the sync and the selection, and
    ///     releases it before it mirrors and emits from the snapshot it took. A changed-build plan syncs without loading a project.
    /// </summary>
    public async Task<EngineResponse> HandleAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        // The session a request names is recorded before its key is taken: the key hashes the session record, so
        // a repeat of the check that first named the session takes the same key as the check that computed the
        // answer and finds it kept, instead of hashing a record its own request had not written yet.
        NoteSession(request);
        var key = CoalesceKey(request);
        if (key is not null && request is EngineRequest.CheckChanges && _initialization is not { IsCompleted: false })
        {
            // A check of every change answers for the files on disk, not for the tree the watcher last
            // reported: a shell write it has not reported yet is in no generation yet, and the kept answer of
            // the tree without it would answer for that tree (and stamp it). The sync confirms the changed
            // set against disk before any kept answer is served, and the key is taken from the generation
            // it publishes. Hooks name their files, which the check itself syncs in by name, so they are
            // not affected by this.
            try
            {
                await _workspace.SyncAndPinAsync([], cancellationToken).ConfigureAwait(false);
                key = CoalesceKey(request);
            }
            catch (FuseException)
            {
                // The check below surfaces this refusal; no kept answer is served for a tree that could
                // not be confirmed.
                key = null;
            }
        }

        if (key is not null && _results.TryGet(key, out var stored))
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
        if (key is not null)
        {
            response = ForRequest(request, response);
            StoreAnswer(key, request, response);
        }

        return response;
    }

    private async Task<EngineResponse> ScheduledAsync(EngineRequest request, CancellationToken cancellationToken, SchedulerLane? laneOverride = null, bool creditSession = true)
    {
        if (request is EngineRequest.Ping or EngineRequest.ShutDown)
            return new EngineResponse.Acknowledged();
        if (request is EngineRequest.Status)
            return await StatusAsync(request, cancellationToken).ConfigureAwait(false);
        if (request is EngineRequest.BeginSession or EngineRequest.EndSession)
            return await SessionAsync(request, cancellationToken).ConfigureAwait(false);

        if (creditSession)
            NoteSession(request);

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

        var lane = laneOverride ?? Scheduler.LaneFor(request);
        var budget = _budget.Assess();
        if (budget.Shed.Count > 0)
            _log.Write($"memory: working set {budget.WorkingSetBytes / (1024 * 1024)} MB of {budget.CeilingBytes / (1024 * 1024)} MB, shedding {string.Join(", ", budget.Shed)}");
        if (budget.Shed.Contains(MemoryBudget.ShedStep.KeptAnswersAndCaches))
        {
            _results.Clear();
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
                return Stamped(ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.AllChanges(), session, analyzers, phases, cancellationToken, maxReported: changes.MaxErrors).ConfigureAwait(false)), changes.StampTree);
            }
            if (request is EngineRequest.CheckPaths paths)
                return await CheckPathsAsync(paths, analyzers, phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.CheckFiles check)
                return await CheckFilesAsync(check, analyzers, phases, cancellationToken).ConfigureAwait(false);
            if (request is EngineRequest.PlanChangedBuild)
                return await PlanChangedBuildAsync(phases, cancellationToken).ConfigureAwait(false);

            if (request is EngineRequest.PlanFailedTests failed)
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

                var pendingFailed = await _planner.SelectFailedAsync(byProject, unknown, phases, cancellationToken).ConfigureAwait(false);
                RememberStatus(configurationBefore, phases);
                held = false;
                lease.Dispose();
                ScheduleBackground();
                return AnswerPlan(await _planner.PrepareAsync(pendingFailed, phases, cancellationToken).ConfigureAwait(false));
            }

            TestScope scope = request switch
            {
                EngineRequest.PlanAffectedTests { Session: { } session } sessioned when SessionEdits.IsValid(session) =>
                    new TestScope.Affected { LeftOut = await OnlyOtherSessionsWroteAsync(session, cancellationToken).ConfigureAwait(false), NamedPaths = sessioned.Projects, BuildOnly = sessioned.BuildOnly },
                EngineRequest.PlanAffectedTests plan => new TestScope.Affected { NamedPaths = plan.Projects, BuildOnly = plan.BuildOnly },
                EngineRequest.PlanAllTests => new TestScope.All(),
                _ => throw new UnreachableException($"{request.GetType().Name} is answered before the scheduler"),
            };
            var pending = await _planner.SelectAsync(scope, phases, cancellationToken).ConfigureAwait(false);

            // The shadow preparation reads only the snapshot the selection took, which is immutable, so it runs after a reader is released and a check from another client does not wait behind the emit.
            // The snapshot is immutable, so no reader is held while it runs.
            RememberStatus(configurationBefore, phases);
            held = false;
            lease.Dispose();
            ScheduleBackground();
            return AnswerPlan(await _planner.PrepareAsync(pending, phases, cancellationToken).ConfigureAwait(false));
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
    ///     A kept or shared answer as <paramref name="request"/> asked for it: stamped with the tree it ends on
    ///     when the request asked for a stamp, and without one when it did not, whatever the check that computed
    ///     it asked for. The errors a stamp names are the same either way.
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
            return Stamped(response, stamp: true);
        return answered.Report.Tree is null
            ? response
            : new EngineResponse.CheckAnswered(answered.Report with { Tree = null, TreeFiles = 0 });
    }

    /// <summary>Keeps a check answer by its content key, as the session file of the session it was answered to.</summary>
    private void StoreAnswer(string key, EngineRequest request, EngineResponse response)
    {
        if (response is not EngineResponse.CheckAnswered answered)
            return;
        var generation = _workspace.PinGeneration();
        var tree = answered.Report.Tree ?? TreeStamp.Of(ChangedPairs(generation.Changed));
        _results.Keep(key, RequestLog.SessionOf(request), generation.Number, tree, answered.Report);
    }

    /// <summary>The request a background re-check of <paramref name="session"/> sends: its files, answered to it, with every analyzer and a tree stamp.</summary>
    private static EngineRequest.CheckFiles BackgroundRequest(BackgroundCheck.SessionFiles session) =>
        new EngineRequest.CheckFiles([.. session.Files.Select(f => f.Absolute)], WaitForLoad: true) { Session = session.Session, StampTree = true };

    /// <summary>The content key a background re-check of <paramref name="session"/> coalesces and is stored by.</summary>
    private string? BackgroundKey(BackgroundCheck.SessionFiles session) => CoalesceKey(BackgroundRequest(session));

    /// <summary>
    ///     The live sessions of the newest generation and the files each wrote in it, from its writers without a
    ///     sync: a session with no changed file in it is not re-checked.
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
        var key = CoalesceKey(request);
        if (key is null || _results.TryGet(key, out _))
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
            response = await _scheduler.RunCoalescedAsync(key, token => ScheduledAsync(request, token, SchedulerLane.Background, creditSession: false), generation.Token).ConfigureAwait(false);
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
        StoreAnswer(key, request, ForRequest(request, response));
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
    ///     The content key a check coalesces by, or null when it does not coalesce: its scope, session, analyzer
    ///     mode and the generation content hash the kept answer keys by. A tree past the kept-answer bound, or a
    ///     changed file that cannot be read, coalesces nothing.
    /// </summary>
    private string? CoalesceKey(EngineRequest request)
    {
        string scopeKey;
        string? session;
        bool compilationWide;
        int maxErrors;
        switch (request)
        {
            case EngineRequest.CheckChanges changes:
                scopeKey = "all";
                session = SessionEdits.IsValid(changes.Session) ? changes.Session : null;
                compilationWide = true;
                maxErrors = changes.MaxErrors;
                break;
            case EngineRequest.CheckFiles files:
                scopeKey = "files:" + string.Join(",", files.Files.Order(StringComparer.Ordinal));
                session = SessionEdits.IsValid(files.Session) ? files.Session : null;
                compilationWide = !files.LeaveOutCompilationWide;
                maxErrors = files.MaxErrors;
                break;
            case EngineRequest.CheckPaths paths:
                scopeKey = "paths:" + string.Join(",", paths.Paths.Order(StringComparer.Ordinal));
                session = SessionEdits.IsValid(paths.Session) ? paths.Session : null;
                compilationWide = true;
                maxErrors = paths.MaxErrors;
                break;
            default:
                return null;
        }

        Generation generation;
        try
        {
            generation = _workspace.PinGeneration();
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }

        if (generation.Changed.Count > 500)
            return null;
        var capKey = maxErrors == 0 ? "cap:default" : $"cap:{maxErrors}";
        var kept = KeptCheck.KeyFor(
            capKey + ":" + scopeKey,
            session,
            compilationWide,
            generation.Head ?? "",
            generation.ConfigurationGeneration,
            generation.BaselineGeneration,
            _sessions.RecordsHash(),
            generation.Changed.Select(path => (path.Relative, path.Absolute)));
        // The stamp a request asks for is no part of the key: it names the same errors, and each waiter of a
        // shared check is stamped, or not, for itself when it is answered.
        return kept is null ? null : $"{scopeKey}|{session ?? ""}|{compilationWide}|{kept}";
    }

    /// <summary>True while the projects are evaluated and first loaded, or while a background load runs.</summary>
    public bool IsLoading => _initialization is { IsCompleted: false } || _preloader.IsRunning;

    /// <summary>The planner the router answers test plans with, for a test that answers a plan alongside a check.</summary>
    internal TestPlanner Planner => _planner;

    /// <summary>The gate the first evaluation and the background load hold while they choose work, for a test that holds it while a background choice is made.</summary>
    internal RequestGate RequestLock => _requestLock;

    /// <summary>The scheduler that admits checks and plans to the reader pool, for a test that holds a reader or reads the queue.</summary>
    internal Scheduler Scheduler => _scheduler;

    /// <summary>The result store answers are kept in, for a test that reads a kept answer or a session file.</summary>
    internal ResultStore Results => _results;

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
    ///     <see cref="ErrorCode.InvalidPath"/>, naming it, before anything is checked.
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
        return Stamped(ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.Files(paths), session, analyzers, phases, cancellationToken, maxReported: check.MaxErrors).ConfigureAwait(false)), check.StampTree);
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

    /// <summary>
    ///     The check answer with the tree stamp the command line prints, from the changed set the check just synced, so
    ///     the client does not ask git again. Unchanged when the request did not ask for it or the check was not answered.
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

    /// <summary>
    ///     Answers a session registration without the scheduler, at once: <see cref="SessionEdits"/> applies it under
    ///     its own lock, so a check that reads its writers under that lock sees the registration whole or not at all.
    ///     It waits for no initialization and holds no reader; a registration needs no workspace.
    ///     A check answered after it counts its files as that session's from that answer on.
    /// </summary>
    private Task<EngineResponse> SessionAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(request switch
        {
            EngineRequest.BeginSession begin => BeginSession(begin),
            EngineRequest.EndSession end => EndSession(end),
            _ => throw new UnreachableException($"{request.GetType().Name} is not a session request"),
        });
    }

    /// <summary>The answer to a test plan request.</summary>
    private static EngineResponse AnswerPlan(TestPlanResult result) => ResponseMapper.Answered(result);

    /// <summary>
    ///     Checks the paths <paramref name="check"/> names. A path that is empty is answered with
    ///     <see cref="ErrorCode.InvalidPath"/> before anything is checked; the checker expands the rest.
    /// </summary>
    private async Task<EngineResponse> CheckPathsAsync(EngineRequest.CheckPaths check, AnalyzerRun analyzers, PhaseTimes phases, CancellationToken cancellationToken)
    {
        foreach (var spec in check.Paths)
        {
            if (string.IsNullOrWhiteSpace(spec))
                return new EngineResponse.Unanswered(ErrorCode.InvalidPath, ErrorMessages.EmptyPath);
        }

        return Stamped(ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.NamedPaths(check.Paths), SessionEdits.IsValid(check.Session) ? check.Session : null, analyzers, phases, cancellationToken, maxReported: check.MaxErrors).ConfigureAwait(false)), check.StampTree);
    }

    /// <summary>
    ///     Waits for the background load and warm to stop, after the shutdown token has been cancelled, so the workspace
    ///     is never disposed under a project that is still loading or warming.
    /// </summary>
    public Task WaitForPreloadAsync() => Task.WhenAll(_preloader.WaitAsync(), _warmer.WaitAsync(), _background.WaitAsync());

    public void Dispose()
    {
        _background.Dispose();
        _scheduler.Dispose();
        _workspace.Dispose();
        _requestLock.Dispose();
    }
}
