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
///     the request lock, and routes each to the feature that answers it.
/// </summary>
internal sealed class RequestRouter : IDisposable
{
    private readonly EngineLog _log;
    private readonly RepoWorkspace _workspace;
    private readonly Checker _checker;
    private readonly TestPlanner _planner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Preloader _preloader;
    private readonly RequestLog _requestLog;
    private Task? _initialization;
    private CancellationToken _shutdown;

    public RequestRouter(RepoRoot root, EngineLog log)
    {
        _log = log;
        _workspace = new RepoWorkspace(root, log.Write);
        _checker = new Checker(_workspace);
        _planner = new TestPlanner(_workspace);
        _preloader = new Preloader(_workspace, _gate, log);
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
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _gate.Release();
        }

        _preloader.Schedule(_shutdown);
    }

    /// <summary>
    ///     Answers one request. A ping or a shutdown is answered at once; a check or a test plan waits for initialization
    ///     (a check that does not wait for the load is refused while loading), then for the request lock, then runs.
    /// </summary>
    public async Task<EngineResponse> HandleAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (request is EngineRequest.Ping or EngineRequest.ShutDown)
            return new EngineResponse.Acknowledged();

        var initialization = _initialization ?? Task.CompletedTask;
        if (!initialization.IsCompleted && request is EngineRequest.CheckChanges { WaitForLoad: false } or EngineRequest.CheckFiles { WaitForLoad: false })
            return new EngineResponse.Unanswered(ErrorCode.Loading, "fuse is still loading this repository; the next check will include these changes");
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
            return new EngineResponse.Unanswered(ErrorCode.LoadFailed, $"fuse could not evaluate the repository's projects: {e.Message}");
        }

        if (_workspace.Graph.Projects.Count == 0)
            return new EngineResponse.Unanswered(ErrorCode.NoProjects, _workspace.Graph.Failures.Count > 0
                ? $"no C# project could be evaluated: {_workspace.Graph.Failures[0]}"
                : "no C# projects (.csproj) found in this repository");

        var phases = new PhaseTimes();
        var queued = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Gate, queued);
        var started = Environment.TickCount64;
        try
        {
            // Each case carries its scope, which becomes the feature's own.
            return request switch
            {
                EngineRequest.CheckChanges => ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.AllChanges(), phases, cancellationToken).ConfigureAwait(false)),
                EngineRequest.CheckFiles check => ResponseMapper.Answered(await _checker.CheckAsync(new CheckScope.Files(check.Files), phases, cancellationToken).ConfigureAwait(false)),
                EngineRequest.PlanAffectedTests => ResponseMapper.Answered(await _planner.PlanAsync(new TestScope.Affected(), phases, cancellationToken).ConfigureAwait(false)),
                EngineRequest.PlanAllTests => ResponseMapper.Answered(await _planner.PlanAsync(new TestScope.All(), phases, cancellationToken).ConfigureAwait(false)),
                _ => throw new UnreachableException($"{request.GetType().Name} is answered before the request lock"),
            };
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
            _requestLog.Write(request, phases, Environment.TickCount64 - started);
            _gate.Release();
            _preloader.Schedule(_shutdown);
        }
    }

    /// <summary>
    ///     Waits for a background load to stop, after the shutdown token has been cancelled, so the workspace is never
    ///     disposed under a project that is still loading.
    /// </summary>
    public Task WaitForPreloadAsync() => _preloader.WaitAsync();

    public void Dispose() => _workspace.Dispose();
}
