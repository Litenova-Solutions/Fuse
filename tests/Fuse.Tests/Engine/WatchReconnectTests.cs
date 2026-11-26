using System.IO.Pipes;
using System.Text;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     <c>fuse watch</c> subscribes again after the push stream drops: it prints the events from the next
///     subscription too, and ends clean only when cancelled. A pipe server stands in for the engine: it drops the
///     first subscription after one event and holds the later ones.
/// </summary>
public class WatchReconnectTests
{
    [Fact]
    public async Task Watch_subscribes_again_after_the_push_stream_drops()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        using var engine = new DroppingEngine(repo.Root);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new LockedWriter();
        var watching = WatchOperation.RunAsync(repo.Root, "worker-1", output, cancel.Token);

        await PollAsync(output, "generation 1");
        await PollAsync(output, "generation 2");
        await cancel.CancelAsync();

        var result = await watching.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(Outcome.Clean, result.Outcome);
    }

    /// <summary>A pipe server under the root's engine pipe name that drops its first subscription after one event and holds the later ones.</summary>
    private sealed class DroppingEngine : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public DroppingEngine(RepoRoot root)
        {
            // The first instance exists before the constructor returns, so the client finds the pipe and starts no engine.
            var first = NewServer(root);
            _serving = ServeAsync(root, first, _stop.Token);
        }

        private static NamedPipeServerStream NewServer(RepoRoot root) =>
            new(root.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        /// <summary>One pushed session event at <paramref name="generation"/>, as the wire carries it.</summary>
        private static string Pushed(long generation) =>
            ProtocolJson.Serialize(new EngineResponse.WatchEventPushed(
                new WatchEvent("session", Session: "worker-1", Generation: generation, Tree: "0123456789ab")));

        private static async Task ServeAsync(RepoRoot root, NamedPipeServerStream pipe, CancellationToken stop)
        {
            var connections = 0;
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stop);
                    var next = NewServer(root);
                    connections++;
                    await PipeFraming.ReadLineAsync(pipe, stop);
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(Pushed(connections) + "\n"), stop);
                    await pipe.FlushAsync(stop);
                    if (connections == 1)
                    {
                        await pipe.DisposeAsync();
                        pipe = next;
                        continue;
                    }

                    // A later subscription holds until the watch is cancelled and disconnects.
                    await PipeFraming.ReadLineAsync(pipe, stop);
                    await pipe.DisposeAsync();
                    pipe = next;
                }
                catch (Exception e) when (e is OperationCanceledException or IOException)
                {
                    await pipe.DisposeAsync();
                    if (stop.IsCancellationRequested)
                        return;
                    pipe = NewServer(root);
                }
            }

            await pipe.DisposeAsync();
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _serving.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            _stop.Dispose();
        }
    }

    /// <summary>Waits until the streamed output holds <paramref name="text"/>.</summary>
    private static async Task PollAsync(LockedWriter output, string text)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (!output.ToString().Contains(text, StringComparison.Ordinal))
        {
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"the watch never printed \"{text}\"; it printed:\n{output}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>What a watch prints, held behind a lock so a test reads it while the stream writes it.</summary>
    private sealed class LockedWriter : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_text)
                _text.Append(value);
        }

        public override void Write(string? value)
        {
            lock (_text)
                _text.Append(value);
        }

        public override void WriteLine(string? value)
        {
            lock (_text)
                _text.Append(value).Append('\n');
        }

        public override string ToString()
        {
            lock (_text)
                return _text.ToString();
        }
    }
}
