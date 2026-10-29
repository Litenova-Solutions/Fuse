using Fuse.Engine;
using Fuse.Telemetry;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A session registration waits for the request lock, so none begins or ends mid-check. While the first evaluation
///     holds the lock, a registration queues behind it instead of answering at once.
/// </summary>
public class SessionGateTests
{
    [Fact]
    public async Task BeginSession_waits_for_the_request_lock()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new EngineLog(repo.Root.StateDirectory);
        using var router = new RequestRouter(repo.Root, log);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var initializing = router.InitializeAsync(shutdown.Token);
        try
        {
            // Let the first evaluation take the lock before the registration queues behind it.
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.True(router.IsLoading, "the engine finished loading before the registration was sent");
            var registering = router.HandleAsync(
                new EngineRequest.BeginSession("w1", ["Lib/**"]) { BuildId = EngineVersion.Build },
                TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);

            Assert.False(registering.IsCompleted, "the registration answered while the engine still held the request lock");
            await initializing;
            Assert.IsType<EngineResponse.Acknowledged>(await registering);
        }
        finally
        {
            await shutdown.CancelAsync();
            await router.WaitForPreloadAsync();
        }
    }

    [Fact]
    public async Task EndSession_waits_for_the_request_lock()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new EngineLog(repo.Root.StateDirectory);
        using var router = new RequestRouter(repo.Root, log);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var initializing = router.InitializeAsync(shutdown.Token);
        try
        {
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.True(router.IsLoading, "the engine finished loading before the registration was sent");
            var registering = router.HandleAsync(
                new EngineRequest.EndSession("w1") { BuildId = EngineVersion.Build },
                TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);

            Assert.False(registering.IsCompleted, "the registration answered while the engine still held the request lock");
            await initializing;
            Assert.IsType<EngineResponse.Unanswered>(await registering);
        }
        finally
        {
            await shutdown.CancelAsync();
            await router.WaitForPreloadAsync();
        }
    }
}
