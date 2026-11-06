using System.Text;
using Fuse.Operations;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     <c>fuse watch --session</c> streams the stored answer of one session from its session file: it names the
///     first answer it finds, waits for one when none is stored yet, prints each newer answer as it is written,
///     and ends when it is cancelled. Without a session it is a usage failure.
/// </summary>
public class WatchTests
{
    [Fact]
    public async Task Watch_streams_each_stored_answer_until_it_is_cancelled()
    {
        using var repo = FixtureRepo.CreateStandard();
        WriteSessionFile(repo, """
            {"session":"worker-1","generation":1,"tree":"0123456789ab","errorCount":1,"errors":[{"path":"Lib/Calc.cs","line":10,"column":5,"id":"CS0103","message":"the name does not exist","fromAnalyzer":false,"text":"Lib/Calc.cs(10,5): error CS0103: the name does not exist"}]}
            """);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new LockedWriter();
        var watching = WatchOperation.RunAsync(repo.Root, "worker-1", output, cancel.Token);

        await PollAsync(output, "CS0103");
        Assert.Contains("1 error(s) introduced; generation 1; tree 0123456789ab", output.ToString(), StringComparison.Ordinal);

        WriteSessionFile(repo, """
            {"session":"worker-1","generation":2,"tree":"ba9876543210","errorCount":0,"errors":[]}
            """);
        await PollAsync(output, "no errors introduced; generation 2");
        await cancel.CancelAsync();

        var result = await watching.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(Outcome.Clean, result.Outcome);
    }

    [Fact]
    public async Task Watch_waits_for_the_first_stored_answer()
    {
        using var repo = FixtureRepo.CreateStandard();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var output = new LockedWriter();
        var watching = WatchOperation.RunAsync(repo.Root, "worker-1", output, cancel.Token);

        await PollAsync(output, "no stored answer yet");
        WriteSessionFile(repo, """
            {"session":"worker-1","generation":1,"tree":"0123456789ab","errorCount":1,"errors":[{"path":"Lib/Calc.cs","line":10,"column":5,"id":"CS1002","message":"; expected","fromAnalyzer":false,"text":"Lib/Calc.cs(10,5): error CS1002: ; expected"}]}
            """);
        await PollAsync(output, "CS1002");
        await cancel.CancelAsync();

        var result = await watching.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(Outcome.Clean, result.Outcome);
    }

    [Fact]
    public async Task Watch_without_a_session_is_a_usage_failure()
    {
        using var repo = FixtureRepo.CreateStandard();

        var result = await FuseProcess.RunAsync(repo.Path, null, "watch");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("fuse watch --session", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Watch_keeps_streaming_in_a_process_until_it_is_stopped()
    {
        using var repo = FixtureRepo.CreateStandard();
        WriteSessionFile(repo, """
            {"session":"worker-1","generation":1,"tree":"0123456789ab","errorCount":0,"errors":[]}
            """);

        await using var client = FuseProcess.Start(repo.Path, "watch", "--session", "worker-1");
        client.Send(null);
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        Assert.False(client.HasExited);
    }

    private static void WriteSessionFile(FixtureRepo repo, string content)
    {
        var directory = Path.Combine(repo.Root.StateDirectory, "sessions");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "worker-1.json"), content);
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
