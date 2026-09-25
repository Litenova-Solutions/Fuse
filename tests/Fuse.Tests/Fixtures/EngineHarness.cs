using Fuse.Check;
using Fuse.Protocol;
using Fuse.Testing;
using Fuse.Workspace;

namespace Fuse.Tests.Fixtures;

/// <summary>Runs the engine's workspace, checker and test planner in the test process, over a fixture repository.</summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    private EngineHarness(FixtureRepo repo)
    {
        Repo = repo;
        Workspace = new RepoWorkspace(repo.Root, _ => { });
        Checker = new Checker(Workspace);
        Planner = new TestPlanner(Workspace);
        Selector = new TestSelector(Workspace);
    }

    public FixtureRepo Repo { get; }

    public RepoWorkspace Workspace { get; }

    public Checker Checker { get; }

    public TestPlanner Planner { get; }

    public TestSelector Selector { get; }

    public static async Task<EngineHarness> StartAsync(FixtureRepo? repo = null)
    {
        var harness = new EngineHarness(repo ?? FixtureRepo.CreateStandard());
        await harness.Workspace.InitializeAsync(TestContext.Current.CancellationToken);
        return harness;
    }

    /// <summary>Checks the given repository-relative files, the way the post-edit hook does.</summary>
    public Task<CheckReport> CheckAsync(params string[] files) =>
        Checker.CheckAsync(files.Select(Repo.Full).ToList(), TestContext.Current.CancellationToken);

    /// <summary>Checks every change since HEAD, the way the Stop hook does, after letting file events arrive.</summary>
    public async Task<CheckReport> CheckAllAsync()
    {
        await Task.Delay(400, TestContext.Current.CancellationToken);
        return await Checker.CheckAsync(null, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Workspace.Dispose();
        await Task.Delay(50);
        Repo.Dispose();
    }
}
