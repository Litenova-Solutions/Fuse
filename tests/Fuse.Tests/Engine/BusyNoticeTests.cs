using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Fuse.Client;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Requests;
using Fuse.Protocol.Transport;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Engine;

public class BusyNoticeTests
{
    [Fact]
    public void The_callback_prints_once_after_two_seconds_of_waits()
    {
        var output = new StringWriter();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var notice = BusyNotice.For(output, time);

        notice(new EngineResponse.Busy(3, 5000));
        time.Advance(TimeSpan.FromSeconds(1));
        notice(new EngineResponse.Busy(3, 5000));
        Assert.Equal("", output.ToString());

        time.Advance(TimeSpan.FromSeconds(1));
        notice(new EngineResponse.Busy(3, 5000));
        var line = $"fuse: waiting for engine (position 3, about 5 s){Environment.NewLine}";
        Assert.Equal(line, output.ToString());

        notice(new EngineResponse.Busy(2, 4000));
        Assert.Equal(line, output.ToString());
    }

    [Fact]
    public void The_wait_names_the_position_and_the_estimated_wait()
    {
        var output = new StringWriter();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var notice = BusyNotice.For(output, time);
        time.Advance(TimeSpan.FromSeconds(3));

        notice(new EngineResponse.Busy(1, 501));

        Assert.Equal($"fuse: waiting for engine (position 1, about 1 s){Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task The_client_calls_the_callback_on_each_busy_it_retries()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var engine = new SequenceEngine(repo.Root, ["Busy", "Busy", "Acknowledged"]);
        var seen = new List<EngineResponse.Busy>();

        var response = await EngineClient.SendAsync(
            repo.Root,
            new EngineRequest.Ping(),
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken,
            seen.Add);

        Assert.IsType<EngineResponse.Acknowledged>(response);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, busy => Assert.Equal(2, busy.Position));
    }

    [Fact]
    public async Task Without_a_callback_the_client_retries_silently()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var engine = new SequenceEngine(repo.Root, ["Busy", "Acknowledged"]);

        var response = await EngineClient.SendAsync(
            repo.Root,
            new EngineRequest.Ping(),
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        Assert.IsType<EngineResponse.Acknowledged>(response);
    }

    /// <summary>A pipe server under the root's engine pipe name that answers each connection with the next queued line.</summary>
    private sealed class SequenceEngine : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        private readonly ConcurrentQueue<string> _lines;

        public SequenceEngine(RepoRoot root, IEnumerable<string> lines)
        {
            _lines = new ConcurrentQueue<string>(lines.Select(AnswerFor));
            var first = NewServer(root);
            _serving = ServeAsync(root, first, _stop.Token);
        }

        private static string AnswerFor(string kind) => kind switch
        {
            "Busy" => """{"status":"Busy","position":2,"estimatedWaitMs":50}""",
            _ => """{"status":"Acknowledged"}""",
        };

        private static NamedPipeServerStream NewServer(RepoRoot root) =>
            new(root.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private async Task ServeAsync(RepoRoot root, NamedPipeServerStream pipe, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stop);
                    var next = NewServer(root);
                    await PipeFraming.ReadLineAsync(pipe, stop);
                    if (!_lines.TryDequeue(out var line))
                        line = """{"status":"Acknowledged"}""";
                    else if (_lines.IsEmpty)
                        _lines.Enqueue(line);
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), stop);
                    await pipe.FlushAsync(stop);
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
}
