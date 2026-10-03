using System.Diagnostics;
using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol;
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
    private readonly RequestLog _requestLog;
    private readonly SessionEdits _sessions;
    private Task? _initialization;
    private CancellationToken _shutdown;

    public RequestRouter(RepoRoot root, EngineLog log)
    {
        _log = log;
        _workspace = new RepoWorkspace(root, log.Write);
        _sessions = new SessionEdits(root, Path.Combine(root.StateDirectory, "sessions.tsv"));
        _checker = new Checker(_workspace, _sessions);
        _planner = new TestPlanner(_workspace);
        _preloader = new Preloader(_workspace, _requestLock, log);
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
        }
        finally
        {
            _requestLock.Exit();
        }

        _preloader.Schedule(_shutdown);
    }

    /// <summary>
    ///     Answers one request. A ping or a shutdown is answered at once; a check or a test plan waits for initialization
    ///     (a check that does not wait for the load is refused while loading), then for the request lock, then runs. A check
    ///     holds the lock until it is answered; a test plan holds it through the sync and the selection, and releases it
    ///     before it mirrors and emits into the shadow folders from the snapshot it took.
    /// </summary>
    public async Task<EngineResponse> HandleAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (request is EngineRequest.Ping or EngineRequest.ShutDown)
            return new EngineResponse.Acknowledged();

        // Recorded before anything can refuse the check, so a check answered "still loading" still credits the session
        // with its files.
        if (request is EngineRequest.CheckFiles { Session: { } writer } written && SessionEdits.IsValid(writer))
            RecordSession(writer, written.Files);

        var initialization = _initialization ?? Task.CompletedTask;
        if (!initialization.IsCompleted && request is EngineRequest.CheckChanges { WaitForLoad: false } or EngineRequest.CheckFiles { WaitForLoad: false })
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
        var started = Environment.TickCount64;
        var held = true;
        try
        {
            // Each case carries its scope, which becomes the feature's own.
            if (request is EngineRequest.CheckChanges)
                return ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.AllChanges(), phases, cancellationToken).ConfigureAwait(false));
            if (request is EngineRequest.CheckFiles check)
                return await CheckFilesAsync(check, phases, cancellationToken).ConfigureAwait(false);

            TestScope scope = request switch
            {
                EngineRequest.PlanAffectedTests => new TestScope.Affected(),
                EngineRequest.PlanAllTests => new TestScope.All(),
                _ => throw new UnreachableException($"{request.GetType().Name} is answered before the request lock"),
            };
            var pending = await _planner.SelectAsync(scope, phases, cancellationToken).ConfigureAwait(false);

            // The shadow preparation reads only the snapshot the selection took, which is immutable, so it runs after the
            // lock is released and a check from another client does not wait behind the emit.
            held = false;
            Release();
            return ResponseMapper.Answered(await _planner.PrepareAsync(pending, phases, cancellationToken).ConfigureAwait(false));
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

    /// <summary>Releases the request lock and lets the background load continue.</summary>
    private void Release()
    {
        _requestLock.Exit();
        _preloader.Schedule(_shutdown);
    }

    /// <summary>The planner the router answers test plans with, for a test that holds a plan in its shadow preparation.</summary>
    internal TestPlanner Planner => _planner;

    /// <summary>
    ///     Checks the files <paramref name="check"/> names. The wire carries them as strings, absolute or relative to the
    ///     root, in whatever letter case the client wrote; each becomes a <see cref="RepoPath"/> in the spelling the file
    ///     system holds it under, and one that is empty or not a valid path is answered with
    ///     <see cref="ErrorCode.InvalidPath"/>, naming it, before anything is checked.
    /// </summary>
    private async Task<EngineResponse> CheckFilesAsync(EngineRequest.CheckFiles check, PhaseTimes phases, CancellationToken cancellationToken)
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
        return ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.Files(paths), session, phases, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    ///     Waits for a background load to stop, after the shutdown token has been cancelled, so the workspace is never
    ///     disposed under a project that is still loading.
    /// </summary>
    public Task WaitForPreloadAsync() => _preloader.WaitAsync();

    public void Dispose()
    {
        _workspace.Dispose();
        _requestLock.Dispose();
    }
}
