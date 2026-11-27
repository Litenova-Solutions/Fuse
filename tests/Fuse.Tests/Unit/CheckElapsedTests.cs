using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Fuse.Check.Model;
using Fuse.Operations.Check;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
using Fuse.Protocol.Transport;
using Fuse.Tests.Fixtures;
namespace Fuse.Tests.Unit;

/// <summary>A check summary names how long the check took, and a gate answer names how long the gate took.</summary>
public class CheckElapsedTests
{
    [Fact]
    public void A_clean_summary_with_seconds_ends_with_a_duration()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], [], 0, false, 0, 0, 0), seconds: 0.04);

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@" in \d+\.\d s$", result.Text);
    }

    [Fact]
    public void An_error_summary_with_seconds_ends_with_a_duration()
    {
        ReportedError[] errors = [new(new CompilerError("App/Program.cs", 2, 28, "CS1061", "no Add"))];
        var result = CheckOperation.Render(new CheckReport(errors, 3, ["App"], [], 0, false, 1, 1, 0), seconds: 1.26);

        Assert.Equal(1, result.ExitCode);
        Assert.Matches(@" in \d+\.\d s$", result.Text.Split('\n')[^1]);
    }

    [Fact]
    public void A_summary_without_seconds_prints_nothing_new()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], [], 0, false, 0, 0, 0));

        Assert.Equal("fuse: no errors introduced (3 file(s) checked)", result.Text);
    }

    [Fact]
    public async Task Gate_json_reports_a_non_negative_elapsed_ms()
    {
        var line = ProtocolJson.Serialize(new EngineResponse.CheckAnswered(new CheckReport([], 0, [], [], 0, false, 0, 0, 0)));
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var engine = new FakeEngine(repo.Root, line);

        var result = await GateOperation.RunAsync(repo.Root, repo.Path, ["--json"], null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Text);
        Assert.True(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.True(json.RootElement.GetProperty("elapsedMs").GetInt64() >= 0);
        Assert.Matches(@" in \d+\.\d s$", json.RootElement.GetProperty("checkOutput").GetString() ?? "");
    }

    [Fact]
    public async Task A_gate_text_answer_ends_its_check_line_with_a_duration()
    {
        var line = ProtocolJson.Serialize(new EngineResponse.CheckAnswered(new CheckReport([], 0, [], [], 0, false, 0, 0, 0)));
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var engine = new FakeEngine(repo.Root, line);

        var result = await GateOperation.RunAsync(repo.Root, repo.Path, [], null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@" in \d+\.\d s$", result.Text);
    }

    /// <summary>A pipe server under the root's engine pipe name that reads each request and writes <c>line</c> back.</summary>
    private sealed class FakeEngine : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public FakeEngine(RepoRoot root, string line)
        {
            // The first instance exists before the constructor returns, so the client finds the pipe and starts no engine.
            var first = NewServer(root);
            _serving = ServeAsync(root, first, line, _stop.Token);
        }

        private static NamedPipeServerStream NewServer(RepoRoot root) =>
            new(root.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        private static async Task ServeAsync(Fuse.Paths.RepoRoot root, NamedPipeServerStream pipe, string line, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stop);
                    var next = NewServer(root);
                    await PipeFraming.ReadLineAsync(pipe, stop);
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
