using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Telemetry;
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
    public Task<CheckResult> CheckAsync(params string[] files) =>
        Checker.CheckAsync(Files(files), PhaseTimes.None, TestContext.Current.CancellationToken);

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

    /// <summary>The scope of the given repository-relative files, as absolute paths, the way a hook names them.</summary>
    public CheckScope.Files Files(params string[] files) => new([.. files.Select(Repo.Full)]);

    /// <summary>
    ///     The reach of the declaration changes in one repository-relative file, over the projects the checker reaches
    ///     from it: the file's owners and their dependents, loaded first.
    /// </summary>
    public async Task<Reach> ReachAsync(string relative)
    {
        var graph = Workspace.Graph;
        var path = Repo.Full(relative);
        var owners = graph.OwnersOf(path).ToList();
        await Workspace.EnsureLoadedAsync(owners, TestContext.Current.CancellationToken);
        var dependents = owners.SelectMany(graph.DependentsOf).DistinctBy(p => p.Path)
            .Where(p => !owners.Any(o => string.Equals(o.Path, p.Path, StringComparison.OrdinalIgnoreCase))).ToList();
        await Workspace.EnsureLoadedAsync(dependents, TestContext.Current.CancellationToken);
        return await new ChangeReach(Workspace).ReachAsync([path], [.. owners, .. dependents], TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Workspace.Dispose();
        await Task.Delay(50);
        Repo.Dispose();
    }
}
