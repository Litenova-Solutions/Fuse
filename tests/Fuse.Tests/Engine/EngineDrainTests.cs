using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Fuse.Engine;
using Fuse.Engine.Client;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.EndToEnd;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A client of another build gets Restart while the engine drains: the engine finishes the requests it serves,
///     then exits, and the client waits with backoff for the matching engine. A pipe server in the test stands in for
///     a draining engine for the client's waits; the engine's own drain runs against a real engine process.
/// </summary>
public class EngineDrainTests
{
    [Fact]
    public async Task Restart_retries_with_backoff_until_the_answer_arrives()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var engine = new RestartingEngine(repo.Root, restarts: 3, """{"status":"Acknowledged"}""");

        var started = Stopwatch.GetTimestamp();
        var response = await EngineClient.SendAsync(repo.Root, new EngineRequest.Ping(), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.IsType<EngineResponse.Acknowledged>(response);
        Assert.Equal(4, engine.Requests);
        // Backoff waits 300 ms, 600 ms and 1200 ms before the answer; three fixed waits would end after 900 ms.
        Assert.True(elapsed >= TimeSpan.FromMilliseconds(2000), $"elapsed {elapsed.TotalMilliseconds:0} ms, expected at least 2000 ms");
    }

    [Fact]
    public async Task Drain_waits_for_the_requests_it_serves_to_finish()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var log = new EngineLog(repo.Root.StateDirectory);
        var running = new List<Task>();
        var runningLock = new Lock();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (runningLock)
            running.Add(gate.Task);

        var draining = EngineServer.DrainAsync(running, runningLock, log);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(draining.IsCompleted);

        gate.SetResult();
        await draining.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(draining.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Drain_without_requests_returns_without_waiting()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var log = new EngineLog(repo.Root.StateDirectory);

        var draining = EngineServer.DrainAsync([], new Lock(), log);

        await draining.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(draining.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_draining_engine_logs_the_mismatched_build_and_the_client_path()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "check")).ExitCode);
            var request = new EngineRequest.Ping { BuildId = "0.0.0/other", RequestId = $"{Environment.ProcessId}-1" };

            var response = await FuseProcess.SendRawAsync(repo.Root, request);

            Assert.IsType<EngineResponse.Restart>(response);
            Assert.True(await FuseProcess.WaitForExitAsync(repo.Root, TimeSpan.FromSeconds(15)));
            var log = File.ReadAllLines(Path.Combine(repo.Root.StateDirectory, "engine.log"));
            var line = Assert.Single(log, l => l.Contains("client build 0.0.0/other differs", StringComparison.Ordinal));
            Assert.Contains("draining", line, StringComparison.Ordinal);
            using var current = Process.GetCurrentProcess();
            var expected = current.MainModule?.FileName;
            if (expected is not null)
                Assert.Contains(expected, line, StringComparison.Ordinal);
            else
                Assert.Contains("(unknown)", line, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_draining_engine_logs_unknown_when_the_client_names_no_process()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "check")).ExitCode);

            var response = await FuseProcess.SendLineAsync(repo.Root, """{"version":"5.0.0/0","kind":"Check","wait":true}""");

            Assert.IsType<EngineResponse.Restart>(response);
            Assert.True(await FuseProcess.WaitForExitAsync(repo.Root, TimeSpan.FromSeconds(15)));
            var log = File.ReadAllLines(Path.Combine(repo.Root.StateDirectory, "engine.log"));
            var line = Assert.Single(log, l => l.Contains("client build (none) differs", StringComparison.Ordinal));
            Assert.Contains("draining", line, StringComparison.Ordinal);
            Assert.Contains("(unknown)", line, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    /// <summary>A pipe server under the root's engine pipe name that answers Restart <c>restarts</c> times, then <c>answer</c>.</summary>
    private sealed class RestartingEngine : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;
        private readonly int _restarts;
        private readonly string _answer;
        private int _requests;

        /// <summary>How many requests arrived, so the test sees each retry.</summary>
        public int Requests => Volatile.Read(ref _requests);

        public RestartingEngine(RepoRoot root, int restarts, string answer)
        {
            _restarts = restarts;
            _answer = answer;
            // The first instance exists before the constructor returns, so the client finds the pipe and starts no engine.
            var first = NewServer(root);
            _serving = ServeAsync(root, first, _stop.Token);
        }

        private static NamedPipeServerStream NewServer(RepoRoot root) =>
            new(root.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private async Task ServeAsync(RepoRoot root, NamedPipeServerStream pipe, CancellationToken stop)
        {
            var restart = ProtocolJson.Serialize(new EngineResponse.Restart());
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stop);
                    var next = NewServer(root);
                    await PipeFraming.ReadLineAsync(pipe, stop);
                    var seen = Interlocked.Increment(ref _requests);
                    var line = seen <= _restarts ? restart : _answer;
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
