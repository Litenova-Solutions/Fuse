using Fuse.Check.Candidates;
using Fuse.Check.Pipeline;
using Fuse.Engine.Background;
using Fuse.Engine.Scheduling;
using Fuse.Failures;
using Fuse.Inspection;
using Fuse.Judge;
using Fuse.Judge.Model;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Repo.Git;
using Fuse.Repo.Tracking;
using Fuse.Telemetry;
using Fuse.Workspace.Generations;
using Fuse.Workspace.Views;
namespace Fuse.Engine.Routing;

/// <summary>
///     Watches the engine's state and memory: it answers what the engine holds without changing it, refuses the
///     requests the engine cannot run yet, sheds caches past the memory ceiling, stores the status of each
///     completed sync, and keeps the background load, warm and re-check going after each request.
/// </summary>
internal sealed class InspectionRoute
{
    private readonly RepoWorkspace _workspace;
    private readonly Scheduler _scheduler;
    private readonly RequestLog _requestLog;
    private readonly EngineLog _log;
    private readonly CandidateJudge _judge;
    private readonly MemoryBudget _budget;
    private readonly AnswerStore _answers;
    private readonly Checker _checker;
    private readonly BackgroundCheck _background;
    private readonly Preloader _preloader;
    private readonly BaselineWarmer _warmer;

    /// <summary>Guards the last completed sync's status and the distinct touched paths it counted.</summary>
    private readonly Lock _statusLock = new();
    private readonly HashSet<RepoPath> _touchedPaths = [];
    private int _touchedConfigurationGeneration;

    internal InspectionRoute(RepoWorkspace workspace, Scheduler scheduler, RequestLog requestLog, EngineLog log, CandidateJudge judge, MemoryBudget budget, AnswerStore answers, Checker checker, BackgroundCheck background, Preloader preloader, BaselineWarmer warmer)
    {
        _workspace = workspace;
        _scheduler = scheduler;
        _requestLog = requestLog;
        _log = log;
        _judge = judge;
        _budget = budget;
        _answers = answers;
        _checker = checker;
        _background = background;
        _preloader = preloader;
        _warmer = warmer;
    }

    /// <summary>
    ///     Lets the background load, warm and re-check continue after the newest generation, unless the memory
    ///     budget sheds background work at this boundary, which stops the re-check that is running too.
    /// </summary>
    /// <param name="shutdown">The engine's shutdown token, which the background work stops with.</param>
    internal void ScheduleBackground(CancellationToken shutdown)
    {
        if (_budget.Assess().Shed.Contains(MemoryBudget.ShedStep.Background))
        {
            _background.CancelCurrent();
            _log.Write("memory: background load and warm shed at this request boundary");
            return;
        }

        _preloader.Schedule(shutdown);
        _warmer.Schedule(shutdown);
        try
        {
            _background.Schedule(_workspace.PinGeneration().Number, shutdown);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // The workspace is going away with the router; no generation is left to re-check.
        }
    }

    /// <summary>
    ///     Answers what the tree holds, bypassing the scheduler entirely: it syncs under the workspace writer and
    ///     reads the newest generation the sync publishes, naming the reader that runs meanwhile when one does; it
    ///     never waits behind a queued check, and never loads a project and never refuses a repository without one.
    ///     Its status carries the engine's working set, its memory ceiling and its GC memory load.
    /// </summary>
    internal async Task<EngineResponse> StatusAsync(EngineRequest request, Task? initialization, CancellationToken cancellationToken)
    {
        var waiting = initialization ?? Task.CompletedTask;
        if (!waiting.IsCompleted)
        {
            try
            {
                await waiting.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            _requestLog.Write(request, phases, Environment.TickCount64 - started, gateHolder);
        }
    }

    /// <summary>
    ///     Plans a build of the changed projects: it syncs like a check, then plans from the
    ///     graph and the changed files. It never loads a project.
    /// </summary>
    internal async Task<EngineResponse> PlanChangedBuildAsync(PhaseTimes phases, CancellationToken cancellationToken)
    {
        var syncing = phases.Start();
        var generation = await _workspace.SyncAndPinAsync([], cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        var projectFiles = (await GitStatus.ChangedPathsAsync(_workspace.Root, cancellationToken).ConfigureAwait(false))
            .Where(path => PathRules.IsProjectFile(path.Absolute))
            .ToList();
        return new EngineResponse.ChangedBuildAnswered(FeatureReports.ToChangedBuildPlan(ChangedBuildPlanner.Plan(generation.Graph, generation.Changed, projectFiles)));
    }

    /// <summary>Answers the project graph, coupling clusters and test ownership from the generation a sync pins.</summary>
    internal async Task<EngineResponse> GraphAsync(PhaseTimes phases, CancellationToken cancellationToken)
    {
        var syncing = phases.Start();
        var generation = await _workspace.SyncAndPinAsync([], cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Sync, syncing);
        return new EngineResponse.GraphAnswered(FeatureReports.ToGraphReport(GraphAnalysis.Build(generation)));
    }

    /// <summary>
    ///     Answers the projects, files and tests the named paths reach. A path that is empty or not a valid path is
    ///     answered with <see cref="Fuse.Failures.ErrorCode.InvalidPath"/>, naming it, before anything is read.
    /// </summary>
    internal async Task<EngineResponse> ImpactAsync(EngineRequest.Impact impact, PhaseTimes phases, CancellationToken cancellationToken)
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
        var report = await ChangeImpact.BuildAsync(_workspace, generation, impact.Paths, cancellationToken).ConfigureAwait(false);
        phases.Add(Phase.Selection, selecting);
        return new EngineResponse.ImpactAnswered(FeatureReports.ToImpactReport(report));
    }

    /// <summary>Answers a judge request: the verdicts of its candidate patches, ranked.</summary>
    internal async Task<EngineResponse> JudgeAsync(EngineRequest.Judge judge, CancellationToken cancellationToken)
    {
        var candidates = judge.Spec.Candidates.Select(candidate => new JudgeCandidate(candidate.Name, candidate.Diff)).ToList();
        var budget = new JudgeBudget(JudgeLimits.MaxCandidates, JudgeLimits.MaxDiffBytes, JudgeLimits.MaxTotalDiffBytes);
        var verdict = await _judge.RunAsync(
            candidates,
            judge.Spec.Mode == JudgeRequest.JudgeMode.Combine ? JudgeMode.Combine : JudgeMode.Race,
            budget,
            cancellationToken).ConfigureAwait(false);
        return new EngineResponse.JudgeAnswered(FeatureReports.ToJudgeReport(verdict));
    }

    /// <summary>
    ///     Sheds the transient caches and compacts the heap, answering how many bytes of working set that reclaimed
    ///     and what it is after. It holds no reader: shedding is the answer to memory pressure, never another waiter
    ///     for it. The router sheds every worktree below this one first, and schedules the background work after.
    /// </summary>
    internal async Task<EngineResponse> YieldMemoryAsync(EngineRequest request, IReadOnlyList<RequestRouter> worktrees, CancellationToken cancellationToken)
    {
        var phases = new PhaseTimes();
        phases.Add(Phase.Gate, 0);
        var started = Environment.TickCount64;
        try
        {
            var before = Environment.WorkingSet;
            await YieldCachesAsync(worktrees, cancellationToken).ConfigureAwait(false);
            _workspace.YieldMemory();
            var after = Environment.WorkingSet;
            return new EngineResponse.MemoryYielded(Math.Max(0, before - after), after);
        }
        finally
        {
            _requestLog.Write(request, phases, Environment.TickCount64 - started, _scheduler.CurrentHolder);
        }
    }

    /// <summary>
    ///     Stores the status of the sync that just completed, counted the way the code counts each threshold: changed
    ///     sources a project owns, and distinct touched paths against the limit past which projects re-evaluate. The
    ///     distinct count is the router's count of the paths each completed sync held; the views hold the same paths.
    /// </summary>
    /// <param name="reloadMs">How long that sync's reload took, or null when it patched without reloading.</param>
    internal EngineStatus CaptureStatus(long? reloadMs)
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
                TreeStamp.Of(CheckRoute.ChangedPairs(changed)))
            {
                ChangedSourcesInReach = changed.Count(path => generation.Graph.OwnersOf(path).Count > 0),
                GateHolder = _scheduler.CurrentHolder,
                MemoryCeilingBytes = _budget.CeilingBytes,
                MemoryLoadBytes = _budget.Assess().MemoryLoadBytes,
                TouchedCount = _touchedPaths.Count,
                ReloadMs = reloadMs,
                Limits = new EngineLimits(
                    ChangeTracker.MaxPatchedPaths,
                    KeptCheck.MaxChangedFiles,
                    CandidateBinding.WholeProjectThreshold,
                    SolutionViews.MaxTouchedPaths),
            };
            return status;
        }
    }

    /// <summary>Stores the status after a sync, naming its reload when the configuration generation moved during it.</summary>
    internal void RememberStatus(int configurationBefore, PhaseTimes phases)
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
    ///     Answers the refusal while the workspace is still loading or holds no project, or null when the request may run.
    /// </summary>
    /// <param name="request">The request to refuse while the engine is not ready.</param>
    /// <param name="initialization">The router's initialization, or null when it never started one.</param>
    /// <param name="cancellationToken">The token that stops waiting for initialization.</param>
    internal async Task<EngineResponse?> RefusalBeforeReadyAsync(EngineRequest request, Task? initialization, CancellationToken cancellationToken)
    {
        var waiting = initialization ?? Task.CompletedTask;
        if (!waiting.IsCompleted && request is EngineRequest.CheckChanges { WaitForLoad: false } or EngineRequest.CheckFiles { WaitForLoad: false } or EngineRequest.CheckPaths { WaitForLoad: false })
            return new EngineResponse.Unanswered(ErrorCode.Loading, "Fuse is still loading this repository; the next check will include these changes");
        try
        {
            await waiting.WaitAsync(cancellationToken).ConfigureAwait(false);
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
    /// <param name="shutdown">The engine's shutdown token, which stops the background shed of session views.</param>
    internal async Task<EngineResponse?> ShedAsync(SchedulerLane lane, CancellationToken shutdown)
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
            await _workspace.ClearSessionViewsAsync(shutdown).ConfigureAwait(false);
        if (budget.Shed.Contains(MemoryBudget.ShedStep.HeadCompilations))
            await _workspace.ClearHeadCompilationsAsync(shutdown).ConfigureAwait(false);
        if (budget.Shed.Contains(MemoryBudget.ShedStep.IdleConeProjects))
            await _workspace.ClearIdleConeProjectsAsync(shutdown).ConfigureAwait(false);
        if (budget.IsPastCeiling)
        {
            var (position, estimate) = _scheduler.EstimateFor(lane);
            return new EngineResponse.Busy(Math.Max(1, position), Math.Max(estimate, 1000));
        }

        return null;
    }

    /// <summary>Sheds this workspace's caches and every worktree's below it, without collecting: the yield collects once after this returns.</summary>
    private async Task YieldCachesAsync(IReadOnlyList<RequestRouter> worktrees, CancellationToken cancellationToken)
    {
        foreach (var child in worktrees)
            await child.Inspection.YieldCachesAsync(child.WorktreeSnapshot(), cancellationToken).ConfigureAwait(false);
        await _workspace.ClearSessionViewsAsync(cancellationToken).ConfigureAwait(false);
        await _workspace.ClearHeadCompilationsAsync(cancellationToken).ConfigureAwait(false);
        await _workspace.ClearIdleConeProjectsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var overlay in _workspace.Worktrees)
            overlay.TrimTransientCaches(clearSessionViews: true, dropCompiledBaseline: true);
    }
}
