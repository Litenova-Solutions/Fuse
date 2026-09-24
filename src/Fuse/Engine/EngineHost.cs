using Fuse.Check;
using Fuse.Protocol;
using Fuse.Repo;
using Fuse.Testing;
using Fuse.Workspace;

namespace Fuse.Engine;

/// <summary>Owns the warm workspace and answers requests one at a time.</summary>
internal sealed class EngineHost : IDisposable
{
    private readonly RepoRoot _root;
    private readonly EngineLog _log;
    private readonly RepoWorkspace _workspace;
    private readonly Checker _checker;
    private readonly TestPlanner _planner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task? _initialization;

    public EngineHost(RepoRoot root, EngineLog log)
    {
        _root = root;
        _log = log;
        _workspace = new RepoWorkspace(root, log.Write);
        _checker = new Checker(_workspace);
        _planner = new TestPlanner(_workspace);
    }

    /// <summary>Evaluates projects, then preloads the projects that already have uncommitted changes.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken) => _initialization ??= InitializeCoreAsync(cancellationToken);

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
    }

    public async Task<EngineResponse> HandleAsync(EngineRequest request, CancellationToken cancellationToken)
    {
        if (request.Kind is RequestKind.Ping or RequestKind.Shutdown)
            return EngineResponse.Ok();

        var initialization = _initialization ?? Task.CompletedTask;
        if (!initialization.IsCompleted && !request.Wait)
            return EngineResponse.Fail(ErrorCode.Loading, "fuse is still loading this repository; the next check will include these changes");
        try
        {
            await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Write($"initialization failed: {e}");
            return EngineResponse.Fail(ErrorCode.LoadFailed, $"fuse could not evaluate the repository's projects: {e.Message}");
        }

        if (_workspace.Graph.Projects.Count == 0)
            return EngineResponse.Fail(ErrorCode.NoProjects, _workspace.Graph.Failures.Count > 0
                ? $"no C# project could be evaluated: {_workspace.Graph.Failures[0]}"
                : "no C# projects (.csproj) found in this repository");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Environment.TickCount64;
        try
        {
            return request.Kind switch
            {
                RequestKind.Check => new EngineResponse(ResponseStatus.Ok, Check: await _checker.CheckAsync(request.Files, cancellationToken).ConfigureAwait(false)),
                RequestKind.TestPlan => new EngineResponse(ResponseStatus.Ok, Tests: await _planner.PlanAsync(request.AllTests, cancellationToken).ConfigureAwait(false)),
                _ => EngineResponse.Fail(ErrorCode.Internal, $"unknown request {request.Kind}"),
            };
        }
        catch (FuseException e)
        {
            return EngineResponse.Fail(e.Code, e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Write($"{request.Kind} failed: {e}");
            return EngineResponse.Fail(ErrorCode.Internal, $"internal error: {e.Message} (details in {_root.Relative(_log.FilePath)})");
        }
        finally
        {
            _log.Write($"{request.Kind} {(request.Files is null ? "all" : string.Join(",", request.Files.Select(Path.GetFileName)))} took {Environment.TickCount64 - started} ms");
            _gate.Release();
        }
    }

    public void Dispose() => _workspace.Dispose();
}
