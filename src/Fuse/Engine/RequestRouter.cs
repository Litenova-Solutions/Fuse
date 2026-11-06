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
///     Owns the warm workspace and answers requests: it initializes the workspace, admits one request at a time through
///     the request lock (<see cref="RequestGate"/>), and routes each to the feature that answers it.
/// </summary>
internal sealed class RequestRouter : IDisposable
{
    private readonly EngineLog _log;
    private readonly RepoWorkspace _workspace;
    private readonly Checker _checker;
    private readonly TestPlanner _planner;
    private readonly RequestGate _requestLock = new();
    private readonly Preloader _preloader;
    private readonly BaselineWarmer _warmer;
    private readonly RequestLog _requestLog;
    private readonly SessionEdits _sessions;

    /// <summary>Guards the last completed sync's status and the distinct touched paths it counted.</summary>
    private readonly Lock _statusLock = new();

    private readonly HashSet<RepoPath> _touchedPaths = [];
    private EngineStatus? _lastStatus;
    private int _touchedConfigurationGeneration;
    private Task? _initialization;
    private CancellationToken _shutdown;

    public RequestRouter(RepoRoot root, EngineLog log)
    {
        _log = log;
        _workspace = new RepoWorkspace(root, log.Write);
        _sessions = new SessionEdits(root, Path.Combine(root.StateDirectory, "sessions.tsv"), Path.Combine(root.StateDirectory, "registered-sessions.tsv"), log: log.Write);
        _checker = new Checker(_workspace, _sessions);
        _planner = new TestPlanner(_workspace);
        _preloader = new Preloader(_workspace, _requestLock, log.Write);
        _warmer = new BaselineWarmer(_workspace, _checker, _requestLock, log.Write);
        _requestLog = new RequestLog(log);
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
            _sessions.Forget(_workspace.Tracker.Changed);
            var live = _sessions.LiveRegistrations();
            if (live.Count > 0)
                _log.Write($"registered sessions live: {string.Join(", ", live.Select(r => r.Session[SessionEdits.RegisteredPrefix.Length..]).Order(StringComparer.Ordinal))}");
            var owners = _workspace.Tracker.Changed.SelectMany(_workspace.Graph.OwnersOf).DistinctBy(p => p.Path).ToList();
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

        _preloader.Schedule(_shutdown);
        _warmer.Schedule(_shutdown);
    }

    /// <summary>
    ///     Answers one request. A ping or a shutdown is answered at once; a status answer is read from the last
    ///     completed sync at once when the request lock is held, naming the holder, and otherwise syncs ahead of queued
    ///     checks; a session registration waits ahead of queued checks and still holds the lock, so none begins or ends
    ///     mid-check; a check or a test plan waits for initialization (a check that does not wait for the load is refused
    ///     while loading), then for the request lock, then runs. A check's wait and analyzer budget start when it holds
    ///     the lock, not when it queued. A check holds the lock until it is answered; a test plan holds it through the
    ///     sync and the selection, and releases it before it mirrors and emits into the shadow folders from the snapshot
    ///     it took. A changed-build plan syncs under the lock without loading a project.
    /// </summary>
    public async Task<EngineResponse> HandleAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (request is EngineRequest.Ping or EngineRequest.ShutDown)
            return new EngineResponse.Acknowledged();
        if (request is EngineRequest.Status)
            return await StatusAsync(request, cancellationToken).ConfigureAwait(false);
        if (request is EngineRequest.BeginSession or EngineRequest.EndSession)
            return await SessionAsync(request, cancellationToken).ConfigureAwait(false);

        // Recorded before anything can refuse the check, so a check answered "still loading" still credits the session
        // with its files.
        if (request is EngineRequest.CheckFiles { Session: { } writer } written && SessionEdits.IsValid(writer))
            RecordSession(writer, written.Files);
        if (request is EngineRequest.CheckChanges { IsStopping: true, Session: { } stopping } && SessionEdits.IsValid(stopping))
            _sessions.MarkStopped(stopping);

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

        var phases = new PhaseTimes();
        var queued = Stopwatch.StartNew();
        var gateHolder = await _requestLock.EnterAsync(RequestLog.KindOf(request), cancellationToken).ConfigureAwait(false);
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
                Release();
                return AnswerPlan(await _planner.PrepareAsync(pendingFailed, phases, cancellationToken).ConfigureAwait(false));
            }

            TestScope scope = request switch
            {
                EngineRequest.PlanAffectedTests { Session: { } session } sessioned when SessionEdits.IsValid(session) =>
                    new TestScope.Affected { LeftOut = await OnlyOtherSessionsWroteAsync(session, cancellationToken).ConfigureAwait(false), NamedPaths = sessioned.Projects, BuildOnly = sessioned.BuildOnly },
                EngineRequest.PlanAffectedTests plan => new TestScope.Affected { NamedPaths = plan.Projects, BuildOnly = plan.BuildOnly },
                EngineRequest.PlanAllTests => new TestScope.All(),
                _ => throw new UnreachableException($"{request.GetType().Name} is answered before the request lock"),
            };
            var pending = await _planner.SelectAsync(scope, phases, cancellationToken).ConfigureAwait(false);

            // The shadow preparation reads only the snapshot the selection took, which is immutable, so it runs after the
            // lock is released and a check from another client does not wait behind the emit.
            RememberStatus(configurationBefore, phases);
            held = false;
            Release();
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
                    Release();
            }
        }
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
        await _workspace.SyncAsync([], cancellationToken).ConfigureAwait(false);
        // Scoping lives in SessionEdits, so a plan and a stop check leave out the same files.
        return _sessions.OnlyOtherSessionsWrote(_workspace.Tracker.Changed, session);
    }

    /// <summary>Releases the request lock and lets the background load and warm continue.</summary>
    private void Release()
    {
        _requestLock.Exit();
        _preloader.Schedule(_shutdown);
        _warmer.Schedule(_shutdown);
    }

    /// <summary>True while the projects are evaluated and first loaded, or while a background load runs.</summary>
    public bool IsLoading => _initialization is { IsCompleted: false } || _preloader.IsRunning;

    /// <summary>The planner the router answers test plans with, for a test that holds a plan in its shadow preparation.</summary>
    internal TestPlanner Planner => _planner;

    /// <summary>The request lock, for a test that holds it while it asks for status or queues a check behind it.</summary>
    internal RequestGate RequestLock => _requestLock;

    /// <summary>
    ///     Stores the status of the sync that just completed, counted the way the code counts each threshold: changed
    ///     sources a project owns, and distinct touched paths against the limit past which projects re-evaluate. The
    ///     distinct count is the router's count of the paths each completed sync held; the views hold the same paths.
    /// </summary>
    /// <param name="reloadMs">How long that sync's reload took, or null when it patched without reloading.</param>
    private EngineStatus CaptureStatus(long? reloadMs)
    {
        var changed = _workspace.Tracker.Changed;
        lock (_statusLock)
        {
            if (_workspace.ConfigurationGeneration != _touchedConfigurationGeneration)
            {
                // The touched set resets only when it passed its limit and the next sync re-evaluated; a reload that
                // did not pass it keeps the paths it already held.
                if (_touchedPaths.Count > SolutionViews.MaxTouchedPaths)
                    _touchedPaths.Clear();
                _touchedConfigurationGeneration = _workspace.ConfigurationGeneration;
            }

            _touchedPaths.UnionWith(changed);
            var status = new EngineStatus(
                Environment.ProcessId,
                Environment.WorkingSet,
                changed.Count,
                _workspace.Tracker.Head,
                TreeStamp.Of(ChangedPairs(changed)))
            {
                ChangedSourcesInReach = changed.Count(path => _workspace.Graph.OwnersOf(path).Count > 0),
                TouchedCount = _touchedPaths.Count,
                ReloadMs = reloadMs,
            };
            _lastStatus = status;
            return status;
        }
    }

    /// <summary>The last completed sync's status with the current holder named, or null when no sync has completed.</summary>
    private EngineStatus? LastStatus(string holder)
    {
        lock (_statusLock)
            return _lastStatus is { } status ? status with { GateHolder = holder } : null;
    }

    /// <summary>Stores the status after a gated sync, naming its reload when the configuration generation moved during it.</summary>
    private void RememberStatus(int configurationBefore, PhaseTimes phases)
    {
        long? reloadMs = null;
        if (_workspace.ConfigurationGeneration != configurationBefore)
        {
            var sync = phases.All.FirstOrDefault(phase => phase.Phase == Phase.Sync);
            reloadMs = sync.Phase is null ? 0 : (long)Math.Round(sync.Ms);
        }

        CaptureStatus(reloadMs);
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
    ///     Answers what the tree holds. When the request lock is held and a sync has completed, it answers at once from
    ///     that sync and names the holder; it never waits behind a check. Otherwise it waits ahead of queued checks,
    ///     syncs, and reads the process, its working set and the changed files. It never loads a project and never
    ///     refuses a repository without one.
    /// </summary>
    private async Task<EngineResponse> StatusAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (_requestLock.CurrentHolder is { } heldBy && LastStatus(heldBy) is { } lastCompleted)
        {
            _requestLog.Write(request, new PhaseTimes(), tookMs: 0, heldBy);
            return new EngineResponse.StatusAnswered(lastCompleted);
        }

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

            // The holder that kept status from a sync of its own may have finished while initialization ran.
            if (_requestLock.CurrentHolder is { } holderAfterInit && LastStatus(holderAfterInit) is { } lastAfterInit)
            {
                _requestLog.Write(request, new PhaseTimes(), tookMs: 0, holderAfterInit);
                return new EngineResponse.StatusAnswered(lastAfterInit);
            }
        }

        var phases = new PhaseTimes();
        var queued = Stopwatch.StartNew();
        var gateHolder = await _requestLock.EnterPriorityAsync(RequestLog.KindOf(request), cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Gate, queued);
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
                Release();
            }
        }
    }

    /// <summary>
    ///     Plans a build of the changed projects: it syncs under the request lock like a check, then plans from the
    ///     graph and the changed files. It never loads a project.
    /// </summary>
    private async Task<EngineResponse> PlanChangedBuildAsync(PhaseTimes phases, CancellationToken cancellationToken)
    {
        var syncing = phases.Start();
        await _workspace.SyncAsync([], cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        var projectFiles = (await GitStatus.ChangedPathsAsync(_workspace.Root, cancellationToken).ConfigureAwait(false))
            .Where(path => PathRules.IsProjectFile(path.Absolute))
            .ToList();
        return new EngineResponse.ChangedBuildAnswered(ChangedBuildPlanner.Plan(_workspace.Graph, _workspace.Tracker.Changed, projectFiles));
    }

    /// <summary>
    ///     The check answer with the tree stamp the command line prints, from the changed set the check just synced, so
    ///     the client does not ask git again. Unchanged when the request did not ask for it or the check was not answered.
    /// </summary>
    private EngineResponse Stamped(EngineResponse response, bool stamp)
    {
        if (!stamp || response is not EngineResponse.CheckAnswered answered)
            return response;
        var changed = _workspace.Tracker.Changed;
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
    ///     Answers a session registration through the request lock, ahead of queued checks, so no registration begins or
    ///     ends mid-check: a check takes its session exclusion at the start and re-reads writers later, and a registration
    ///     between the two would shield errors neither read shielded. It waits for no initialization beyond the lock,
    ///     which the first evaluation holds; a registration needs no workspace.
    /// </summary>
    private async Task<EngineResponse> SessionAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        await _requestLock.EnterPriorityAsync(RequestLog.KindOf(request), cancellationToken).ConfigureAwait(false);
        try
        {
            return request switch
            {
                EngineRequest.BeginSession begin => BeginSession(begin),
                EngineRequest.EndSession end => EndSession(end),
                _ => throw new UnreachableException($"{request.GetType().Name} is not a session request"),
            };
        }
        finally
        {
            Release();
        }
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
    public Task WaitForPreloadAsync() => Task.WhenAll(_preloader.WaitAsync(), _warmer.WaitAsync());

    public void Dispose()
    {
        _workspace.Dispose();
        _requestLock.Dispose();
    }
}
