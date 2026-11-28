using System.IO.Pipes;
using System.Text;
using Fuse.Client;
using Fuse.Engine.Server;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
using Fuse.Tests.EndToEnd;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

/// <summary>
///     A batch connection answers every request it carries in order, and a watch subscription reads the events
///     published after it subscribed: only its session's, or every event. A slow subscription drops its oldest event
///     instead of slowing the publisher, and a cancelled subscription ends.
/// </summary>
public class EngineServerStreamTests
{
    [Fact]
    public async Task A_session_subscription_reads_only_its_sessions_events()
    {
        var hub = new WatchHub();
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        subscription.CancelAfter(TimeSpan.FromMilliseconds(500));
        await using var reader = hub.SubscribeAsync("a", all: false, subscription.Token).GetAsyncEnumerator(subscription.Token);
        hub.Publish(new WatchEvent.CheckUpdated { Session = "a", Message = "first" });
        hub.Publish(new WatchEvent.CheckUpdated { Session = "b", Message = "other" });
        hub.Publish(new WatchEvent.Reloaded { Message = "wide" });
        hub.Publish(new WatchEvent.CheckUpdated { Session = "a", Message = "second" });

        Assert.Equal(["first", "second"], (await ReadAllAsync(reader)).Select(e => e.Message));
    }

    [Fact]
    public async Task An_all_subscription_reads_every_event_in_order()
    {
        var hub = new WatchHub();
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        subscription.CancelAfter(TimeSpan.FromMilliseconds(500));
        await using var reader = hub.SubscribeAsync(session: null, all: true, subscription.Token).GetAsyncEnumerator(subscription.Token);
        hub.Publish(new WatchEvent.CheckUpdated { Session = "a", Message = "first" });
        hub.Publish(new WatchEvent.CheckUpdated { Session = "b", Message = "second" });
        hub.Publish(new WatchEvent.Reloaded { Message = "third" });
        hub.Publish(new WatchEvent.GuardViolated { Session = "a", Message = "fourth" });

        Assert.Equal(["first", "second", "third", "fourth"], (await ReadAllAsync(reader)).Select(e => e.Message));
    }

    [Fact]
    public async Task A_full_subscription_drops_its_oldest_events()
    {
        var hub = new WatchHub();
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        subscription.CancelAfter(TimeSpan.FromMilliseconds(500));
        await using var reader = hub.SubscribeAsync("a", all: false, subscription.Token).GetAsyncEnumerator(subscription.Token);
        for (var i = 0; i < WatchHub.KeptEvents + 3; i++)
            hub.Publish(new WatchEvent.CheckUpdated { Session = "a", Message = $"m{i}" });

        var read = await ReadAllAsync(reader);

        Assert.Equal(WatchHub.KeptEvents, read.Count);
        Assert.Equal("m3", read[0].Message);
        Assert.Equal($"m{WatchHub.KeptEvents + 2}", read[^1].Message);
    }

    [Fact]
    public async Task Cancelling_ends_the_subscription()
    {
        var hub = new WatchHub();
        using var subscription = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var reader = hub.SubscribeAsync("a", all: false, subscription.Token).GetAsyncEnumerator(subscription.Token);
        subscription.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await reader.MoveNextAsync(); });
        // Publishing after the subscription ended reaches no one and throws nothing.
        hub.Publish(new WatchEvent.CheckUpdated { Session = "a", Message = "after" });
    }

    [Fact]
    public async Task A_batch_of_three_requests_on_one_connection_gets_three_answers_in_order()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "check")).ExitCode);
            await using var connection = await ConnectAsync(repo.Root.PipeName);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            EngineRequest[] requests = [new EngineRequest.Ping(), new EngineRequest.Status(), new EngineRequest.Ping()];
            var answers = new List<EngineResponse?>();
            foreach (var request in requests)
            {
                var stamped = request with { BuildId = EngineVersion.Build, RequestId = EngineClient.NextRequestId(), WaitMs = 30000, WorktreeRoot = repo.Root.Path };
                var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(stamped) + "\n");
                await connection.WriteAsync(bytes, timeout.Token);
                await connection.FlushAsync(timeout.Token);
                var line = await PipeFraming.ReadLineAsync(connection, timeout.Token);
                Assert.NotNull(line);
                answers.Add(ProtocolJson.ReadResponse(line));
            }

            Assert.IsType<EngineResponse.Acknowledged>(answers[0]);
            Assert.IsType<EngineResponse.StatusAnswered>(answers[1]);
            Assert.IsType<EngineResponse.Acknowledged>(answers[2]);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    /// <summary>Connects to the engine's pipe, retrying a connection the pipe server resets while it is replaced, as the client does.</summary>
    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName)
    {
        for (var attempt = 0; ; attempt++)
        {
            var connection = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await connection.ConnectAsync(5000, TestContext.Current.CancellationToken);
                return connection;
            }
            catch (IOException) when (attempt < 4)
            {
                await connection.DisposeAsync();
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }
    }

    /// <summary>Reads every event published so far: the token ends the trailing read once the channel holds nothing more.</summary>
    private static async Task<List<WatchEvent>> ReadAllAsync(IAsyncEnumerator<WatchEvent> reader)
    {
        var read = new List<WatchEvent>();
        try
        {
            while (await reader.MoveNextAsync())
                read.Add(reader.Current);
        }
        catch (OperationCanceledException)
        {
            // The token ended the trailing read; the events read so far are the answer.
        }

        return read;
    }

}
