using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Status and a session registration bypass the scheduler: they answer while every reader is held, status
///     naming the holder through its <c>GateHolder</c> and reading the newest generation, and a registration
///     answering without waiting for the held reader or for a check queued behind it.
/// </summary>
public class StatusUnderLoadTests
{
    [Fact]
    public async Task Status_answers_while_a_reader_is_held_and_names_the_holder()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new EngineLog(repo.Root.StateDirectory);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scheduler = new Scheduler(1);
        var router = new RequestRouter(repo.Root, log, scheduler, new MemoryBudget());
        SchedulerLease? lease = null;
        try
        {
            await router.InitializeAsync(shutdown.Token);
            repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a - b;");
            var first = await PollStatusAsync(router, 1);
            Assert.Equal(1, first.ChangedFileCount);
            Assert.Null(first.GateHolder);

            lease = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);
            repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            await Task.Delay(400, TestContext.Current.CancellationToken);

            var held = await StatusAsync(router, TimeSpan.FromSeconds(5));

            Assert.Equal("CheckFiles", held.GateHolder);
            Assert.Equal(1, held.ChangedFileCount);
            Assert.Equal(1, held.ChangedSourcesInReach);
            Assert.NotEqual(first.Tree, held.Tree);

            lease.Dispose();
            lease = null;

            var after = await PollStatusAsync(router, 1);
            Assert.Null(after.GateHolder);
            Assert.Equal(held.Tree, after.Tree);
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
    public async Task A_registration_answers_without_waiting_for_a_held_reader_or_a_queued_check()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new EngineLog(repo.Root.StateDirectory);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var scheduler = new Scheduler(1);
        var router = new RequestRouter(repo.Root, log, scheduler, new MemoryBudget());
        SchedulerLease? lease = null;
        try
        {
            await router.InitializeAsync(shutdown.Token);
            repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a - b;");

            lease = await scheduler.EnterAsync(SchedulerLane.Command, "CheckFiles", 0, TestContext.Current.CancellationToken);
            var queuedCheck = router.HandleAsync(
                new EngineRequest.CheckFiles(["Lib/Calc.cs"], WaitForLoad: true) { BuildId = EngineVersion.Build, RequestId = "queued-behind-holder" },
                TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.False(queuedCheck.IsCompleted);

            var registered = await router.HandleAsync(
                new EngineRequest.BeginSession("worker-1", ["Lib/**"]) { BuildId = EngineVersion.Build, RequestId = "registration-bypass" },
                TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.IsType<EngineResponse.Acknowledged>(registered);
            Assert.False(queuedCheck.IsCompleted);

            lease.Dispose();
            lease = null;

            var checkedResponse = await queuedCheck.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.IsType<EngineResponse.CheckAnswered>(checkedResponse);
        }
        finally
        {
            lease?.Dispose();
            await shutdown.CancelAsync();
            await router.WaitForPreloadAsync();
            router.Dispose();
        }
    }

    private static async Task<EngineStatus> StatusAsync(RequestRouter router, TimeSpan? timeout = null)
    {
        var request = new EngineRequest.Status { BuildId = EngineVersion.Build, RequestId = EngineLogRequestId() };
        var pending = router.HandleAsync(request, TestContext.Current.CancellationToken);
        var response = timeout is { } limit
            ? await pending.WaitAsync(limit, TestContext.Current.CancellationToken)
            : await pending;
        return Assert.IsType<EngineResponse.StatusAnswered>(response).Engine;
    }

    private static string EngineLogRequestId() => Guid.NewGuid().ToString("N");

    private static async Task<EngineStatus> PollStatusAsync(RequestRouter router, int expected)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (true)
        {
            var status = await StatusAsync(router);
            if (status.ChangedFileCount == expected && status.GateHolder is null)
                return status;
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"changed files stayed at {status.ChangedFileCount}, expected {expected}");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}
