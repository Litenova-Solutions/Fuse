using System.Diagnostics;
using Fuse.Identity;
using Fuse.Check.Sessions;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Telemetry;
using Fuse.Testing.Filtering;
using Fuse.Testing.Model;
using Fuse.Testing.Model.Caching;
using Fuse.Testing.Planning;
using Fuse.Testing.Selection;
using Fuse.Workspace.Generations;
namespace Fuse.Engine.Routing;

/// <summary>
///     Answers the test plan requests: plans of the affected tests, of all tests and of the failed tests, and stores
///     each finished slice verdict in the slice cache.
/// </summary>
internal sealed class TestPlanRoute
{
    private readonly TestPlanner _planner;
    private readonly RepoWorkspace _workspace;
    private readonly SessionEdits _sessions;
    private readonly EngineLog _log;

    internal TestPlanRoute(TestPlanner planner, RepoWorkspace workspace, SessionEdits sessions, EngineLog log)
    {
        _planner = planner;
        _workspace = workspace;
        _sessions = sessions;
        _log = log;
    }

    /// <summary>Selects the tests that failed last time and are still in their projects, naming the projects that are gone.</summary>
    internal async Task<PendingTestPlan> SelectFailedAsync(EngineRequest.PlanFailedTests failed, PhaseTimes phases, CancellationToken cancellationToken)
    {
        if (failed.Clean)
            _planner.SliceCache.Clear();
        var (byProject, unknown) = FailedTestProjects(failed);

        return await _planner.SelectFailedAsync(byProject, unknown, phases, cancellationToken, noCache: failed.NoCache).ConfigureAwait(false);
    }

    /// <summary>Selects the affected or all tests for <paramref name="request"/>, with the cache cleared when it asked for a clean plan.</summary>
    internal async Task<(PendingTestPlan Pending, bool NoCache)> SelectScopedAsync(EngineRequest request, PhaseTimes phases, CancellationToken cancellationToken)
    {
        // MaxFailures is enforced where tests run, not while planning, so the plan carries no trace of it.
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
            EngineRequest.PlanAffectedTests { Session: { } session } sessioned when SessionId.IsValid(session) =>
                new TestScope.Affected { LeftOut = await OnlyOtherSessionsWroteAsync(session, cancellationToken).ConfigureAwait(false), NamedPaths = sessioned.Projects, BuildOnly = sessioned.BuildOnly, NoCache = sessioned.NoCache },
            EngineRequest.PlanAffectedTests plan => new TestScope.Affected { NamedPaths = plan.Projects, BuildOnly = plan.BuildOnly, NoCache = plan.NoCache },
            EngineRequest.PlanAllTests allTests => new TestScope.All { NoCache = allTests.NoCache },
            _ => throw new UnreachableException($"{request.GetType().Name} is answered before the scheduler"),
        };
        var pending = await _planner.SelectAsync(scope, phases, cancellationToken).ConfigureAwait(false);
        if (request is EngineRequest.PlanAllTests scoped && scoped.Projects.Length > 0)
            pending = FilterAllToProjects(pending, scoped.Projects);
        return (pending, noCache);
    }

    /// <summary>
    ///     Limits an every-test plan to the named test projects. Every-test plans answer at once with no preparation,
    ///     so only that shape is filtered; a plan that needs preparation stays whole.
    /// </summary>
    /// <param name="pending">The planned every-test plan.</param>
    /// <param name="projects">Absolute paths naming test project files, solution files, or directories holding them.</param>
    private static PendingTestPlan FilterAllToProjects(PendingTestPlan pending, string[] projects)
    {
        if (pending is not PendingTestPlan.Ready ready)
            return pending;
        // Paths the command named limit the plan to the test projects they name; none naming one is refused.
        var named = NamedTestProjects.Resolve(ready.Generation.Graph, projects);
        var keep = named.Select(n => n.Path).ToHashSet();
        var runs = ready.Result.Runs.Where(r => keep.Contains(r.Project)).ToList();
        if (runs.Count == ready.Result.Runs.Count)
            return pending;
        var counter = new TestCounter();
        var nodes = ready.Generation.Graph.Projects.Where(p => keep.Contains(p.Path)).ToDictionary(p => p.Path);
        var selected = runs.Sum(r => nodes.TryGetValue(r.Project, out var node) ? counter.TestsIn(node).Count : 0);
        var gatedProjects = ready.Result.ContainerGatedProjects.Where(p => named.Any(n => n.Name == p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var excludedGatedTests = ready.Generation.Graph.Projects
            .Where(p => p.IsTest && ready.Result.ContainerGatedProjects.Contains(p.Name, StringComparer.Ordinal) && !keep.Contains(p.Path))
            .Sum(p => counter.TestsIn(p).Count);
        var gatedTests = Math.Max(0, ready.Result.ContainerGatedTests - excludedGatedTests);
        var summary = $"ran every test in {runs.Count} test project(s)";
        if (gatedTests > 0)
            summary += $"; skipped {gatedTests} test(s) in {string.Join(", ", gatedProjects)} (no container runtime)";
        summary += $"; in {string.Join(", ", named.Select(n => n.Name))}";
        var result = new TestPlanResult(runs, selected, ready.Result.TotalTests, summary)
        {
            ContainerGatedTests = gatedTests,
            ContainerGatedProjects = gatedProjects,
        };
        return PendingTestPlan.Of(result, ready.Generation);
    }

    /// <summary>Prepares the shadow runs of a selected plan and answers it.</summary>
    /// <param name="noCache">True when the plan skipped the slice cache, so no run answers from it.</param>
    internal async Task<EngineResponse> PrepareAsync(PendingTestPlan pending, bool noCache, PhaseTimes phases, CancellationToken cancellationToken) =>
        AnswerPlan(await _planner.PrepareAsync(pending, phases, cancellationToken).ConfigureAwait(false), noCache);

    /// <summary>
    ///     Stores one finished test slice's verdict in the slice cache, without the scheduler and at once: the cache
    ///     applies a store under its own locks, so a plan that reads it sees the verdict whole or not at all. It
    ///     waits for no initialization and holds no reader; a store needs only the shared state directory. A line
    ///     with no slice key is ignored, and the answer is always acknowledged, because a store never fails a run.
    /// </summary>
    internal EngineResponse.Acknowledged StoreSlice(EngineRequest.StoreSliceResult store)
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
    ///     The changed files whose writers are all other sessions than <paramref name="session"/>, after a sync, so a plan
    ///     for the session leaves their changes out. A file no session is credited with, or one the session wrote, stays.
    /// </summary>
    private async Task<IReadOnlySet<RepoPath>> OnlyOtherSessionsWroteAsync(string session, CancellationToken cancellationToken)
    {
        var generation = await _workspace.SyncAndPinAsync([], cancellationToken).ConfigureAwait(false);
        // Scoping lives in SessionEdits, so a plan and a stop check leave out the same files.
        return _sessions.OnlyOtherSessionsWrote(generation.Changed, session);
    }

    /// <summary>The answer to a test plan request.</summary>
    /// <param name="noCache">True when the plan skipped the slice cache, so no run answers from it.</param>
    private static EngineResponse.PlanAnswered AnswerPlan(TestPlanResult result, bool noCache = false) => new EngineResponse.PlanAnswered(ResponseMapper.Plan(result, noCache));
}
