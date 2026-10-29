using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

public class ChangedBuildPlanTests
{
    [Fact]
    public async Task A_change_in_a_library_builds_the_highest_dependents()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a - b;");
        await PollChangedAsync(engine, 1);

        var plan = Assert.IsType<EngineResponse.ChangedBuildAnswered>(await engine.SendAsync(new EngineRequest.PlanChangedBuild())).Plan;

        Assert.Equal([repo.PathOf("App.Tests/App.Tests.csproj"), repo.PathOf("Lib.Tests/Lib.Tests.csproj")], plan.BuildProjects.Select(repo.Root.PathOf));
        Assert.Equal(["Lib"], plan.ChangedInProjects);
        Assert.Equal(1, plan.ChangedFileCount);
        Assert.Matches("^[0-9a-f]{12}$", plan.TreeHash);
        Assert.Equal("", plan.FallbackReason);
        Assert.Equal(0, plan.UnownedFileCount);
    }

    [Fact]
    public async Task Changes_in_two_unrelated_libraries_build_both()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Multi/Shape.cs", "=> count;", "=> count + 1;");
        repo.Replace("Strict/Thing.cs", "=> 1;", "=> 2;");
        await PollChangedAsync(engine, 2);

        var plan = Assert.IsType<EngineResponse.ChangedBuildAnswered>(await engine.SendAsync(new EngineRequest.PlanChangedBuild())).Plan;

        Assert.Equal([repo.PathOf("Multi/Multi.csproj"), repo.PathOf("Strict/Strict.csproj")], plan.BuildProjects.Select(repo.Root.PathOf));
        Assert.Equal(["Multi", "Strict"], plan.ChangedInProjects);
    }

    [Fact]
    public async Task A_clean_tree_builds_nothing()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        var plan = Assert.IsType<EngineResponse.ChangedBuildAnswered>(await engine.SendAsync(new EngineRequest.PlanChangedBuild())).Plan;

        Assert.Empty(plan.BuildProjects);
        Assert.Empty(plan.ChangedInProjects);
        Assert.Equal(0, plan.ChangedFileCount);
        Assert.Equal("e3b0c44298fc", plan.TreeHash);
        Assert.Equal("", plan.FallbackReason);
    }

    [Fact]
    public async Task A_changed_project_file_builds_the_whole_solution()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a - b;");
        repo.Replace("Lib/Lib.csproj", "</Project>", "<!-- build -->\n</Project>");
        await PollChangedAsync(engine, 1);

        var plan = Assert.IsType<EngineResponse.ChangedBuildAnswered>(await engine.SendAsync(new EngineRequest.PlanChangedBuild())).Plan;

        Assert.Empty(plan.BuildProjects);
        Assert.Equal("project files changed (Lib/Lib.csproj)", plan.FallbackReason);
    }

    [Fact]
    public async Task A_source_in_no_project_builds_the_whole_solution()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Write("Loose/Orphan.cs", "public static class Orphan\n{\n    public static int Value() => 1;\n}\n");
        await PollChangedAsync(engine, 1);

        var plan = Assert.IsType<EngineResponse.ChangedBuildAnswered>(await engine.SendAsync(new EngineRequest.PlanChangedBuild())).Plan;

        Assert.Empty(plan.BuildProjects);
        Assert.Equal("1 changed file(s) in no project", plan.FallbackReason);
        Assert.Equal(1, plan.UnownedFileCount);
    }

    [Fact]
    public void A_plan_request_writes_and_reads_back()
    {
        var request = Assert.IsType<EngineRequest.PlanChangedBuild>(
            ProtocolJson.ReadRequest(ProtocolJson.Serialize(new EngineRequest.PlanChangedBuild { BuildId = "6.0.0/abc" })));

        Assert.Equal("6.0.0/abc", request.BuildId);
    }

    [Fact]
    public void A_plan_without_the_new_fields_reads_their_defaults()
    {
        var answered = Assert.IsType<EngineResponse.ChangedBuildAnswered>(
            ProtocolJson.ReadResponse("""{"status":"ChangedBuildAnswered","plan":{"buildProjects":[],"changedInProjects":[],"changedFileCount":0,"treeHash":"e3b0c44298fc"}}"""));

        Assert.Equal("", answered.Plan.FallbackReason);
        Assert.Equal(0, answered.Plan.UnownedFileCount);
    }

    /// <summary>Waits until the tracker's sync sees <paramref name="expected"/> changed files, which the watcher's events reach on their own time.</summary>
    private static async Task PollChangedAsync(InProcessRequestRouter engine, int expected)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (true)
        {
            var status = Assert.IsType<EngineResponse.StatusAnswered>(await engine.SendAsync(new EngineRequest.Status()));
            if (status.Engine.ChangedFileCount == expected)
                return;
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"changed files stayed at {status.Engine.ChangedFileCount}, expected {expected}");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
