using Fuse.Engine.Routing;
using Fuse.Engine.Scheduling;
using Fuse.Engine.Server;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
using Fuse.Telemetry;
using Fuse.Testing.Caching;
using Fuse.Testing.Model;
using Fuse.Testing.Model.Caching;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     The 7.0.0 requests through the engine's request loop in this process: a memory yield, status beside a
///     queued check, repair packets on a brief check, the fast path on a body edit, and the slice cache on
///     test plans.
/// </summary>
public class RequestRouterSwarmTests
{
    [Fact]
    public async Task YieldMemory_answers_with_reclaimed_bytes_and_working_set()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        var yielded = Assert.IsType<EngineResponse.MemoryYielded>(await engine.SendAsync(new EngineRequest.YieldMemory()));

        Assert.True(yielded.ReclaimedBytes >= 0);
        Assert.True(yielded.WorkingSetAfterBytes > 0);
    }

    [Fact]
    public async Task Status_answers_during_a_queued_check()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new EngineLog(repo.Root.StateDirectory);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scheduler = new Scheduler(1);
        var router = new RequestRouter(repo.Root, log, scheduler, new MemoryBudget(), new WatchHub());
        SchedulerLease? lease = null;
        try
        {
            await router.InitializeAsync(shutdown.Token);
            repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");

            lease = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);
            var check = router.HandleAsync(
                new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { BuildId = EngineVersion.Build, RequestId = "queued-check" },
                TestContext.Current.CancellationToken);
            var deadline = Environment.TickCount64 + 5000;
            while (scheduler.QueuedCount == 0 && Environment.TickCount64 < deadline)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.Equal(1, scheduler.QueuedCount);

            var status = Assert.IsType<EngineResponse.StatusAnswered>(await router.HandleAsync(
                new EngineRequest.Status { BuildId = EngineVersion.Build, RequestId = "status-during-queue" },
                TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            Assert.Equal("CheckFiles", status.Engine.GateHolder);
            Assert.False(check.IsCompleted);

            lease.Dispose();
            lease = null;

            Assert.IsType<EngineResponse.CheckAnswered>(await check.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        finally
        {
            lease?.Dispose();
            await shutdown.CancelAsync();
            await router.WaitForPreloadAsync();
            router.Dispose();
        }
    }

    [Fact]
    public async Task Brief_check_attaches_repair_packets_to_a_CS1061()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Ad(1, 2)");

        var brief = Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(
            new EngineRequest.CheckFiles([repo.Full("App/Program.cs")], WaitForLoad: true) { Brief = true })).Report;
        var error = Assert.Single(brief.Errors);
        Assert.Equal("CS1061", error.Error.Id);
        Assert.NotNull(error.Repair);
        Assert.Contains(error.Repair.Candidates, candidate => candidate.Contains("Add", StringComparison.Ordinal));
        var window = Assert.IsType<string>(error.Repair.SourceWindow);
        Assert.Contains("calc.Ad", window, StringComparison.Ordinal);

        var plain = Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(
            new EngineRequest.CheckFiles([repo.Full("App/Program.cs")], WaitForLoad: true) { Brief = false })).Report;
        Assert.Null(Assert.Single(plain.Errors).Repair);
    }

    [Fact]
    public async Task InteractiveFast_check_reports_body_errors_and_falls_back_on_declaration_changes()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        repo.Replace("Strict/Thing.cs", "public static int Value() => 1;", "public static int Value()\n    {\n        int unused = 0;\n        return missingVar;\n    }");
        var body = Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(
            new EngineRequest.CheckFiles([repo.Full("Strict/Thing.cs")], WaitForLoad: true) { InteractiveFast = true })).Report;
        Assert.Contains(body.Errors, e => e.Error.Id == "CS0103" && e.Error.Path == "Strict/Thing.cs");
        Assert.Contains(body.Errors, e => e.Error.Id == "CS0219" && e.Error.Path == "Strict/Thing.cs");
        Assert.Equal(0, body.DependentProjectsChecked);

        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var fallback = Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(
            new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { InteractiveFast = true })).Report;
        Assert.Contains(fallback.Errors, e => e.Error.Message.Contains("'Add'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_plan_serves_a_cached_slice_and_honours_NoCache_and_Clean()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        var cancellationToken = TestContext.Current.CancellationToken;
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        // The check syncs the edit by name, so every plan below sees it without waiting for the watcher.
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true)));

        var pending = await engine.Planner.SelectAsync(new TestScope.Affected(), PhaseTimes.None, cancellationToken);
        var prepared = await engine.Planner.PrepareAsync(pending, PhaseTimes.None, cancellationToken);
        var key = Assert.Single(prepared.Runs).SliceKey;
        Assert.NotNull(key);
        engine.Planner.SliceCache.Store(new SliceCacheResult(key, "Lib.Tests/Lib.Tests.csproj", "Lib.Tests", "", Passed: 3, Failed: 0, Skipped: 0, DurationMs: 4, CachedAtUtc: DateTime.UtcNow, Failures: []));

        var cached = Assert.IsType<EngineResponse.PlanAnswered>(await engine.SendAsync(new EngineRequest.PlanAffectedTests())).Plan;
        var hit = Assert.Single(cached.Runs);
        Assert.True(hit.FromCache);
        Assert.Contains("3 passed", Assert.IsType<string>(hit.CacheReason), StringComparison.Ordinal);

        var fresh = Assert.IsType<EngineResponse.PlanAnswered>(await engine.SendAsync(new EngineRequest.PlanAffectedTests { NoCache = true })).Plan;
        var skipped = Assert.Single(fresh.Runs);
        Assert.False(skipped.FromCache);
        Assert.Null(skipped.CacheReason);

        var cleaned = Assert.IsType<EngineResponse.PlanAnswered>(await engine.SendAsync(new EngineRequest.PlanAffectedTests { Clean = true })).Plan;
        Assert.False(Assert.Single(cleaned.Runs).FromCache);
        Assert.False(Directory.Exists(SliceTestCache.DirectoryFor(repo.Root.SharedStateDirectory)));

        var after = Assert.IsType<EngineResponse.PlanAnswered>(await engine.SendAsync(new EngineRequest.PlanAffectedTests())).Plan;
        Assert.False(Assert.Single(after.Runs).FromCache);
    }
}
