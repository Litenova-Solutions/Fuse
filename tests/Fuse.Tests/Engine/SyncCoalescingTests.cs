using Fuse.Workspace.Generations;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

public class SyncCoalescingTests
{
    [Fact]
    public async Task Sixteen_concurrent_pathless_syncs_run_at_most_two_rounds()
    {
        using var repo = FixtureRepo.CreateStandard();
        using var workspace = new RepoWorkspace(repo.Root, _ => { });
        await workspace.InitializeAsync(TestContext.Current.CancellationToken);
        var roundsBefore = workspace.PathlessSyncRounds;

        var syncs = Enumerable.Range(0, 16)
            .Select(_ => workspace.SyncAndPinAsync([], TestContext.Current.CancellationToken))
            .ToList();
        var generations = await Task.WhenAll(syncs);

        var rounds = workspace.PathlessSyncRounds - roundsBefore;
        Assert.True(rounds is >= 1 and <= 2, $"16 concurrent pathless syncs ran {rounds} rounds, at most 2");
        Assert.True(generations.Select(generation => generation.Number).Distinct().Count() <= 2, "every waiter shares the running or the queued sync");
    }

    [Fact]
    public async Task A_sync_with_known_paths_runs_its_own_round_and_confirms_its_files()
    {
        using var repo = FixtureRepo.CreateStandard();
        using var workspace = new RepoWorkspace(repo.Root, _ => { });
        await workspace.InitializeAsync(TestContext.Current.CancellationToken);
        repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a - b;");
        var path = repo.PathOf("Lib/Calc.cs");
        var roundsBefore = workspace.PathlessSyncRounds;

        var pathless = Enumerable.Range(0, 16)
            .Select(_ => workspace.SyncAndPinAsync([], TestContext.Current.CancellationToken))
            .ToList();
        var known = await workspace.SyncAndPinAsync([path], TestContext.Current.CancellationToken);
        await Task.WhenAll(pathless);

        var rounds = workspace.PathlessSyncRounds - roundsBefore;
        Assert.True(rounds is >= 1 and <= 2, $"16 concurrent pathless syncs ran {rounds} rounds, at most 2");
        Assert.Contains(path, known.Changed);
    }
}
