using System.Collections.Concurrent;
using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;

namespace Fuse.Tests.Engine;

/// <summary>
///     The coverage guard: a reached project no view holds fails the check instead of narrowing it, a multi-targeted, a
///     test and a Razor-shaped project never count as missing while loaded, an unreached project counts never, and a
///     check that races the background load still reports every dependent.
/// </summary>
public class CheckCoverageTests
{
    [Fact]
    public async Task Coverage_names_a_reached_project_the_view_lacks()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new ConcurrentQueue<string>();
        using var workspace = new RepoWorkspace(repo.Root, log.Enqueue);
        var cancellationToken = TestContext.Current.CancellationToken;
        await workspace.InitializeAsync(cancellationToken);
        var tests = workspace.Graph.Find(repo.PathOf("Lib.Tests/Lib.Tests.csproj"))!;
        await workspace.EnsureLoadedAsync([workspace.Graph.Find(repo.PathOf("Lib/Lib.csproj"))!], cancellationToken);

        Assert.Equal(["Lib.Tests"], Checker.Uncovered([tests], workspace.Current, workspace.Baseline).Select(node => node.Name));

        await workspace.EnsureLoadedAsync([tests], cancellationToken);

        Assert.Empty(Checker.Uncovered([tests], workspace.Current, workspace.Baseline));
    }

    [Fact]
    public async Task Coverage_ignores_a_project_the_check_does_not_reach()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new ConcurrentQueue<string>();
        using var workspace = new RepoWorkspace(repo.Root, log.Enqueue);
        var cancellationToken = TestContext.Current.CancellationToken;
        await workspace.InitializeAsync(cancellationToken);
        var lib = workspace.Graph.Find(repo.PathOf("Lib/Lib.csproj"))!;
        await workspace.EnsureLoadedAsync([lib], cancellationToken);

        // Lib.Tests is not loaded, but the check does not reach it, so nothing is missing.
        Assert.Empty(Checker.Uncovered([lib], workspace.Current, workspace.Baseline));
    }

    [Fact]
    public async Task Coverage_holds_for_multi_target_test_and_razor_shaped_projects()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new ConcurrentQueue<string>();
        using var workspace = new RepoWorkspace(repo.Root, log.Enqueue);
        var cancellationToken = TestContext.Current.CancellationToken;
        await workspace.InitializeAsync(cancellationToken);
        var multi = workspace.Graph.Find(repo.PathOf("Multi/Multi.csproj"))!;
        var tests = workspace.Graph.Find(repo.PathOf("Lib.Tests/Lib.Tests.csproj"))!;
        var app = workspace.Graph.Find(repo.PathOf("App/App.csproj"))!;

        // A Razor component is an additional document in its view, whatever SDK builds its project.
        repo.Write("App/Card.razor", "<h1>Card</h1>\n");
        await workspace.SyncAsync([repo.PathOf("App/Card.razor")], cancellationToken);
        await workspace.EnsureLoadedAsync([multi, tests, app], cancellationToken);

        // Multi builds into one project per framework; at least one in each view covers it.
        Assert.Empty(Checker.Uncovered([multi, tests, app], workspace.Current, workspace.Baseline));
        Assert.NotEmpty(workspace.Current.GetDocumentIdsWithFilePath(repo.Full("App/Card.razor")));

        var report = await new Checker(workspace, new SessionEdits(workspace.Root, null))
            .CheckAsync(new CheckScope.Files([repo.PathOf("App/Card.razor")]), PhaseTimes.None, cancellationToken);

        Assert.Empty(report.Errors);
        Assert.DoesNotContain(log, l => l.StartsWith("check coverage:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_check_that_races_the_background_load_reports_every_dependent()
    {
        using var repo = FixtureRepo.CreateStandard();
        for (var i = 0; i < 5; i++)
        {
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            // The initialization schedules the background load of the dependents; the check below races it.
            await using var engine = await InProcessRequestRouter.StartAsync(repo);
            var report = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync(repo.Full("Lib/Calc.cs"))).Report;
            Assert.Contains(report.Errors, e => e.Error.Path == "App/Program.cs");
            Assert.Contains(report.Errors, e => e.Error.Path == "Lib.Tests/CalcTests.cs");
            repo.Replace("Lib/Calc.cs", "public int Plus(", "public int Add(");
        }
    }
}
