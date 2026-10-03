using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Telemetry;
using Fuse.Testing;
using Fuse.Workspace;

namespace Fuse.Tests.Fixtures;

/// <summary>Runs the engine's workspace, checker and test planner in the test process, over a fixture repository.</summary>
internal sealed class InProcessEngine : IAsyncDisposable
{
    private InProcessEngine(FixtureRepo repo)
    {
        Repo = repo;
        Workspace = new RepoWorkspace(repo.Root, _ => { });
        Sessions = new SessionEdits(repo.Root, null);
        Checker = new Checker(Workspace, Sessions);
        Planner = new TestPlanner(Workspace);
        Selector = new TestSelector(Workspace, TimeProvider.System);
    }

    public FixtureRepo Repo { get; }

    public RepoWorkspace Workspace { get; }

    public Checker Checker { get; }

    /// <summary>Which sessions wrote each file, kept in memory; a test records what a post-edit hook would.</summary>
    public SessionEdits Sessions { get; }

    public TestPlanner Planner { get; }

    public TestSelector Selector { get; }

    public static async Task<InProcessEngine> StartAsync(FixtureRepo? repo = null)
    {
        var engine = new InProcessEngine(repo ?? FixtureRepo.CreateStandard());
        await engine.Workspace.InitializeAsync(TestContext.Current.CancellationToken);
        return engine;
    }

    /// <summary>Checks the given repository-relative files, the way the post-edit hook does.</summary>
    public Task<CheckResult> CheckAsync(params string[] files) =>
        Checker.CheckAsync(Files(files), PhaseTimes.None, TestContext.Current.CancellationToken);

    /// <summary>
    ///     Records that <paramref name="session"/> wrote the given repository-relative files and checks them, answered to
    ///     that session, the way the engine answers a post-edit hook whose harness identifies the session.
    /// </summary>
    public Task<CheckResult> CheckAsSessionAsync(string session, params string[] files)
    {
        var scope = Files(files);
        Sessions.Record(session, scope.Paths);
        return Checker.CheckAsync(scope, session, PhaseTimes.None, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     Folds the given repository-relative files into both views at once, the way a check that names them does, so a
    ///     test does not wait for the file watcher to report a file the next check does not name.
    /// </summary>
    public Task SyncAsync(params string[] files) =>
        Workspace.SyncAsync(files.Select(Repo.PathOf), TestContext.Current.CancellationToken);

    /// <summary>Checks the same files and returns the phase times the check recorded.</summary>
    public async Task<(CheckResult Result, IReadOnlyList<(string Phase, double Ms)> Phases)> CheckWithPhasesAsync(params string[] files)
    {
        var phases = new PhaseTimes();
        var result = await Checker.CheckAsync(Files(files), phases, TestContext.Current.CancellationToken);
        return (result, phases.All);
    }

    /// <summary>Checks every change since HEAD, the way the Stop hook does, after letting file events arrive.</summary>
    public async Task<CheckResult> CheckAllAsync()
    {
        await Task.Delay(400, TestContext.Current.CancellationToken);
        return await Checker.CheckAsync(new CheckScope.AllChanges(), PhaseTimes.None, TestContext.Current.CancellationToken);
    }

    /// <summary>The scope of the given repository-relative files, as the engine has it once it has read a hook's request.</summary>
    public CheckScope.Files Files(params string[] files) => new([.. files.Select(Repo.PathOf)]);

    /// <summary>
    ///     The reach of the declaration changes in repository-relative files, passed to the reach in the order given, over
    ///     the projects the checker reaches from them: the files' owners and their dependents, loaded first.
    /// </summary>
    public async Task<Reach> ReachAsync(params string[] relatives)
    {
        var graph = Workspace.Graph;
        var paths = relatives.Select(Repo.PathOf).ToList();
        var owners = paths.SelectMany(graph.OwnersOf).DistinctBy(p => p.Path).ToList();
        await Workspace.EnsureLoadedAsync(owners, TestContext.Current.CancellationToken);
        var dependents = owners.SelectMany(graph.DependentsOf).DistinctBy(p => p.Path)
            .Where(p => !owners.Any(o => o.Path == p.Path)).ToList();
        await Workspace.EnsureLoadedAsync(dependents, TestContext.Current.CancellationToken);
        return await new ChangeReach(Workspace).ReachAsync(paths, [.. owners, .. dependents], TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Workspace.Dispose();
        await Task.Delay(50);
        Repo.Dispose();
    }
}
