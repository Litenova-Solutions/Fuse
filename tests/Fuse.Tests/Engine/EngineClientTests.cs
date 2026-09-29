using System.IO.Pipes;
using System.Text;
using Fuse.Engine.Client;
using Fuse.Failures;
using Fuse.Operations;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Tests.EndToEnd;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     An answer the client cannot read is an answer Fuse could not give: the command exits with 2 and names what
///     happened, and a hook stays silent, rather than either ending with a stack trace. A pipe server in the test stands
///     in for the engine and answers every connection with the same line.
/// </summary>
public class EngineClientTests
{
    [Theory]
    [InlineData("""{"status":"Ok","check":{}}""")]
    [InlineData("""{"report":{"errors":[]},"status":"CheckAnswered"}""")]
    [InlineData("""{"status":"CheckAnswered","report":{"errors":[""")]
    [InlineData("""{"status":"CheckAnswered"}""")]
    [InlineData("""{"status":"CheckAnswered","report":{"filesChecked":1}}""")]
    [InlineData("not json")]
    public async Task An_answer_the_client_cannot_read_is_unanswered(string line)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var engine = new FakeEngine(repo.Root, line);

        var (result, response) = await CheckOperation.RunAsync(repo.Root, ["Lib/Calc.cs"], waitForLoad: true, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var unanswered = Assert.IsType<EngineResponse.Unanswered>(response);
        Assert.Equal(ErrorCode.Internal, unanswered.Code);
        Assert.Contains("answer this client cannot read", unanswered.Message, StringComparison.Ordinal);
        Assert.Contains("engine.log", unanswered.Message, StringComparison.Ordinal);
        Assert.Equal(Outcome.Unanswered, result.Outcome);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task A_plan_the_client_cannot_read_is_unanswered()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var engine = new FakeEngine(repo.Root, """{"status":"PlanAnswered","plan":{"summary":"x"}}""");

        var response = await EngineClient.SendAsync(repo.Root, new EngineRequest.PlanAffectedTests(), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(ErrorCode.Internal, Assert.IsType<EngineResponse.Unanswered>(response).Code);
    }

    [Fact]
    public async Task A_hook_that_gets_an_answer_it_cannot_read_exits_zero_without_output()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "class Calc {}\n" });
        using var engine = new FakeEngine(repo.Root, """{"status":"CheckAnswered","report":{"errors":[""");
        var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["cwd"] = repo.Path,
            ["tool_input"] = new Dictionary<string, string> { ["file_path"] = repo.Full("Lib/Calc.cs") },
        });

        var result = await FuseProcess.RunAsync(repo.Path, payload, "hook", "claude", "post-edit");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout + result.Stderr);
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

        private static async Task ServeAsync(RepoRoot root, NamedPipeServerStream pipe, string line, CancellationToken stop)
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
