using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

public class EngineStatusTests
{
    [Fact]
    public async Task Status_answers_the_process_and_the_tree()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a - b;");

        var status = await PollStatusAsync(engine, 1);

        Assert.Equal(Environment.ProcessId, status.Pid);
        Assert.True(status.WorkingSetBytes > 0);
        Assert.Equal(1, status.ChangedFileCount);
        Assert.Equal(1, status.ChangedSourcesInReach);
        Assert.True(status.TouchedCount >= 1);
        Assert.Null(status.GateHolder);
        Assert.Equal(40, status.Head!.Length);
        Assert.Matches("^[0-9a-f]{12}$", status.Tree);
    }

    [Fact]
    public async Task Status_counts_only_sources_a_project_owns_as_in_reach()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a - b;");
        repo.Write("Loose/Orphan.cs", "public static class Orphan {}\n");
        repo.Write("notes.md", "changed\n");

        var status = await PollStatusAsync(engine, 2);

        Assert.Equal(2, status.ChangedFileCount);
        Assert.Equal(1, status.ChangedSourcesInReach);
    }

    [Fact]
    public async Task Status_answers_without_csharp_projects()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["notes.md"] = "notes\n" });
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Write("Loose/Orphan.cs", "public static class Orphan {}\n");

        var status = await PollStatusAsync(engine, 1);

        Assert.Equal(1, status.ChangedFileCount);
        Assert.Equal(0, status.ChangedSourcesInReach);
        Assert.Matches("^[0-9a-f]{12}$", status.Tree);
    }

    [Fact]
    public void A_status_request_writes_and_reads_back()
    {
        var request = Assert.IsType<EngineRequest.Status>(
            ProtocolJson.ReadRequest(ProtocolJson.Serialize(new EngineRequest.Status { BuildId = "5.7.0/abc" })));

        Assert.Equal("5.7.0/abc", request.BuildId);
    }

    [Fact]
    public void A_status_answer_writes_and_reads_back()
    {
        var answered = Assert.IsType<EngineResponse.StatusAnswered>(
            ProtocolJson.ReadResponse(ProtocolJson.Serialize(new EngineResponse.StatusAnswered(new EngineStatus(4212, 812L * 1024 * 1024, 87, new string('a', 40), "9f2c41aa07b3")
            {
                ChangedSourcesInReach = 80,
                TouchedCount = 120,
                ReloadMs = 45,
                GateHolder = "CheckFiles",
            }))));

        Assert.Equal(4212, answered.Engine.Pid);
        Assert.Equal(812L * 1024 * 1024, answered.Engine.WorkingSetBytes);
        Assert.Equal(87, answered.Engine.ChangedFileCount);
        Assert.Equal(80, answered.Engine.ChangedSourcesInReach);
        Assert.Equal(120, answered.Engine.TouchedCount);
        Assert.Equal(45, answered.Engine.ReloadMs);
        Assert.Equal("CheckFiles", answered.Engine.GateHolder);
        Assert.Equal(new string('a', 40), answered.Engine.Head);
        Assert.Equal("9f2c41aa07b3", answered.Engine.Tree);
    }

    [Fact]
    public void A_status_answer_without_a_head_reads_it_as_null()
    {
        var answered = Assert.IsType<EngineResponse.StatusAnswered>(
            ProtocolJson.ReadResponse("""{"status":"StatusAnswered","engine":{"pid":4212,"workingSetBytes":1,"changedFileCount":0,"tree":"e3b0c44298fc"}}"""));

        Assert.Null(answered.Engine.Head);
        Assert.Equal(0, answered.Engine.ChangedSourcesInReach);
        Assert.Equal(0, answered.Engine.TouchedCount);
        Assert.Null(answered.Engine.ReloadMs);
        Assert.Null(answered.Engine.GateHolder);
    }

    /// <summary>Waits until status sees <paramref name="expected"/> changed files, which the watcher's events reach on their own time.</summary>
    private static async Task<EngineStatus> PollStatusAsync(InProcessRequestRouter engine, int expected)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (true)
        {
            var status = Assert.IsType<EngineResponse.StatusAnswered>(await engine.SendAsync(new EngineRequest.Status())).Engine;
            if (status.ChangedFileCount == expected)
                return status;
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"changed files stayed at {status.ChangedFileCount}, expected {expected}");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
