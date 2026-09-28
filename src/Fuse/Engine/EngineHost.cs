using Fuse.Graph;
using Fuse.Check;
using Fuse.Protocol;
using Fuse.Repo;
using Fuse.Testing;
using Fuse.Workspace;

namespace Fuse.Engine;

/// <summary>Owns the warm workspace and answers requests one at a time.</summary>
internal sealed class EngineHost : IDisposable
{
    private readonly EngineLog _log;
    private readonly RepoWorkspace _workspace;
    private readonly Checker _checker;
    private readonly CheckBatch _batches;
    private readonly TestPlanner _planner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _preloadFailed = new(StringComparer.OrdinalIgnoreCase);
    private Task? _initialization;
    private Task _preload = Task.CompletedTask;
    private CancellationToken _shutdown;

    public EngineHost(RepoRoot root, EngineLog log, TimeSpan? batchWindow = null)
    {
        _log = log;
        _workspace = new RepoWorkspace(root, log.Write);
        _checker = new Checker(_workspace);
        _batches = new CheckBatch(_checker, batchWindow ?? EngineHost.BatchWindow);
        _planner = new TestPlanner(_workspace);
    }

    /// <summary>
    ///     How long a check waits for more requests before it starts, so several agents checking at once are answered
    ///     together instead of one after another. It is one gate wait in the measurements, small enough that a lone
    ///     check pays little for it and long enough to catch the requests of a second agent editing alongside the first.
    /// </summary>
    public static TimeSpan BatchWindow { get; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Evaluates projects, then preloads the projects that already have uncommitted changes.</summary>
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

        SchedulePreload();
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
        catch (FuseException e)
        {
            return EngineResponse.Fail(e.Code, e.Message);
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

        var phases = new PhaseTimes();
        var queued = System.Diagnostics.Stopwatch.StartNew();
        if (request.Kind is RequestKind.Check)
        {
            // A check joins the batch instead of taking the gate: several agents checking at once are answered together,
            // each with the answer a serial check would have given it. A test plan is never batched, because it plans
            // whole projects and mirrors build output, and two of those at once are worse than two queues.
            var report = await _batches.EnqueueAsync(request.Files, phases, cancellationToken).ConfigureAwait(false);
            var total = queued.ElapsedMilliseconds;
            _log.Write($"Check {(request.Files is null ? "all" : string.Join(",", request.Files.Select(Path.GetFileName)))} took {total} ms, batch of {_batches.LastBatchSize} client(s)");
            if (request.RequestId.Length > 0)
                _log.Write(PhaseLine.Format(request.RequestId, request.Kind.ToString(), [.. phases.All, ("total", total)]));
            return new EngineResponse(ResponseStatus.Ok, Check: report);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PhaseTimes.Add(phases, "gate", queued);
        var started = Environment.TickCount64;
        try
        {
            return request.Kind switch
            {
                RequestKind.TestPlan => new EngineResponse(ResponseStatus.Ok, Tests: await _planner.PlanAsync(request.AllTests, phases, cancellationToken).ConfigureAwait(false)),
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
            return EngineResponse.Fail(ErrorCode.Internal, $"internal error: {e.Message} (details in {_log.FilePath})");
        }
        finally
        {
            var took = Environment.TickCount64 - started;
            _log.Write($"{request.Kind} {(request.Files is null ? "all" : string.Join(",", request.Files.Select(Path.GetFileName)))} took {took} ms");
            // One line per request, naming it and timing each phase, so a measurement can be matched to its own call.
            if (request.RequestId.Length > 0)
                _log.Write(PhaseLine.Format(request.RequestId, request.Kind.ToString(), [.. phases.All, ("total", took)]));
            _gate.Release();
            SchedulePreload();
        }
    }

    /// <summary>
    ///     Loads, in the background and one at a time, the projects that depend on projects with uncommitted changes.
    ///     A declaration change has to bind those dependents, and loading them is the slow part of a first check
    ///     (seconds per project); doing it while the agent is busy elsewhere keeps later checks fast. Requests take
    ///     precedence: the gate admits them between project loads.
    /// </summary>
    private void SchedulePreload()
    {
        if (!_preload.IsCompleted || _shutdown.IsCancellationRequested)
            return;
        _preload = Task.Run(PreloadAsync);
    }

    private async Task PreloadAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            ProjectNode? next;
            // The gate only protects choosing the next project; the load itself runs outside it, so requests keep flowing.
            await _gate.WaitAsync(_shutdown).ConfigureAwait(false);
            try
            {
                var graph = _workspace.Graph;
                next = _workspace.Tracker.Changed
                    .SelectMany(graph.OwnersOf)
                    .SelectMany(graph.DependentsOf)
                    .FirstOrDefault(p => !_workspace.IsLoaded(p) && !_preloadFailed.Contains(p.Path));
            }
            finally
            {
                _gate.Release();
            }

            if (next is null)
                return;
            try
            {
                await _workspace.PreloadAsync(next, _shutdown).ConfigureAwait(false);
            }
            catch (FuseException e)
            {
                _preloadFailed.Add(next.Path);
                _log.Write($"preload of {next.Name} skipped: {e.Message}");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _log.Write($"preload failed: {e}");
                return;
            }
        }
    }

    public void Dispose() => _workspace.Dispose();
}
