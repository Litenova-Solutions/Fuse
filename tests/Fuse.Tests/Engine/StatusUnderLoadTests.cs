using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Status answers at once from the last completed sync while the request lock is held, naming the holder, instead
///     of waiting behind the check that holds it.
/// </summary>
public class StatusUnderLoadTests
{
    [Fact]
    public async Task Status_answers_from_the_last_completed_sync_while_a_check_holds_the_lock()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new EngineLog(repo.Root.StateDirectory);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var router = new RequestRouter(repo.Root, log);
        try
        {
            await router.InitializeAsync(shutdown.Token);
            repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a - b;");
            var first = await PollStatusAsync(router, 1);
            Assert.Equal(1, first.ChangedFileCount);
            Assert.Null(first.GateHolder);

            string lastTree;
            await router.RequestLock.EnterAsync("CheckFiles", TestContext.Current.CancellationToken);
            try
            {
                repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");

                var last = await StatusAsync(router, TimeSpan.FromSeconds(5));
                lastTree = last.Tree;

                Assert.Equal("CheckFiles", last.GateHolder);
                Assert.Equal(1, last.ChangedFileCount);
                Assert.Equal(1, last.ChangedSourcesInReach);
            }
            finally
            {
                router.RequestLock.Exit();
            }

            var after = await PollStatusWithTreeAsync(router, lastTree);
            Assert.Null(after.GateHolder);
            Assert.Equal(1, after.ChangedFileCount);
            Assert.NotEqual(lastTree, after.Tree);
        }
        finally
        {
            await shutdown.CancelAsync();
            await router.WaitForPreloadAsync();
            router.Dispose();
        }
    }

    [Fact]
    public async Task A_registration_runs_before_a_queued_check_after_the_holder()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new EngineLog(repo.Root.StateDirectory);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var router = new RequestRouter(repo.Root, log);
        try
        {
            await router.InitializeAsync(shutdown.Token);
            repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a - b;");

            await router.RequestLock.EnterAsync("CheckFiles", TestContext.Current.CancellationToken);
            var queuedCheck = router.HandleAsync(
                new EngineRequest.CheckFiles(["Lib/Calc.cs"], WaitForLoad: true) { BuildId = EngineVersion.Build, RequestId = "queued-behind-holder" },
                TestContext.Current.CancellationToken);
            var registration = router.HandleAsync(
                new EngineRequest.BeginSession("worker-1", ["Lib/**"]) { BuildId = EngineVersion.Build, RequestId = "registration-priority" },
                TestContext.Current.CancellationToken);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            router.RequestLock.Exit();

            var registered = await registration.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.IsType<EngineResponse.Acknowledged>(registered);

            var checkedResponse = await queuedCheck.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.IsType<EngineResponse.CheckAnswered>(checkedResponse);
        }
        finally
        {
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

    /// <summary>Waits until a status from a sync of its own names a tree other than <paramref name="otherTree"/>.</summary>
    private static async Task<EngineStatus> PollStatusWithTreeAsync(RequestRouter router, string otherTree)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (true)
        {
            var status = await StatusAsync(router);
            if (status.GateHolder is null && status.Tree != otherTree)
                return status;
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"tree stayed at {status.Tree}, expected a tree other than {otherTree}");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

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
