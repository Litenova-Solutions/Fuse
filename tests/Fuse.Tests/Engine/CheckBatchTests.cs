using Fuse.Check;
using Fuse.Cli;
using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Several agents checking the same repository at once. The batch coalesces them so the work is shared, and the
///     property under test is that sharing changes nothing: each client gets the answer a serial check would have given it
///     alone, and a client that arrives late is not answered from a snapshot taken before its edit.
/// </summary>
public class CheckBatchTests
{
    [Fact]
    public async Task Four_concurrent_checks_equal_four_serial_checks()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Greet(string name, bool loud);");
        engine.Repo.Replace("App/Report.cs", "Line(int value)", "Line2(int value)");
        engine.Repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Plus(1, 2)");

        var scopes = new IReadOnlyCollection<string>?[]
        {
            [engine.Repo.Full("Lib/Calc.cs")],
            [engine.Repo.Full("Lib/Greeting.cs")],
            [engine.Repo.Full("App/Report.cs")],
            null,
        };

        var batch = new CheckBatch(engine.Checker, TimeSpan.FromMilliseconds(150));
        var batched = await Task.WhenAll(scopes.Select(s => batch.EnqueueAsync(s, null, TestContext.Current.CancellationToken)));
        Assert.Equal(4, batch.LastBatchSize);

        // The same four checks, one after another, each in the state it would have been in on its own.
        var serial = new List<CheckReport>();
        foreach (var scope in scopes)
            serial.Add(await engine.Checker.CheckAsync(scope, null, TestContext.Current.CancellationToken));

        Assert.Equal(4, batched.Length);
        for (var i = 0; i < batched.Length; i++)
            Assert.Equal(Render(serial[i]), Render(batched[i]));
    }

    [Fact]
    public async Task A_single_clients_answer_is_the_same_as_todays()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

        var batch = new CheckBatch(engine.Checker, TimeSpan.FromMilliseconds(50));
        var direct = await engine.Checker.CheckAsync([engine.Repo.Full("Lib/Calc.cs")], null, TestContext.Current.CancellationToken);
        var queued = await batch.EnqueueAsync([engine.Repo.Full("Lib/Calc.cs")], null, TestContext.Current.CancellationToken);

        Assert.Equal(Render(direct), Render(queued));
        Assert.Equal(1, batch.LastBatchSize);
    }

    [Fact]
    public async Task A_late_arrival_is_not_answered_from_the_older_snapshot()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var batch = new CheckBatch(engine.Checker, TimeSpan.FromMilliseconds(50));

        // The first request is queued alone, and its batch reads the tree. The second arrives after that.
        var first = batch.EnqueueAsync([engine.Repo.Full("Lib/Calc.cs")], null, TestContext.Current.CancellationToken);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var firstReport = await first;
        engine.Repo.Replace("Lib/Calc.cs", "public int Mul(", "public int Times(");
        var second = await batch.EnqueueAsync([engine.Repo.Full("Lib/Calc.cs")], null, TestContext.Current.CancellationToken);

        // The first answer cannot know about the rename that came after its batch read the tree.
        Assert.DoesNotContain(firstReport.Introduced, d => d.Message.Contains("'Mul'", StringComparison.Ordinal));
        Assert.Contains(second.Introduced, d => d.Message.Contains("'Mul'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_disconnected_client_is_dropped_and_the_rest_are_answered()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Greet(string name, bool loud);");
        using var gone = new CancellationTokenSource();
        var batch = new CheckBatch(engine.Checker, TimeSpan.FromMilliseconds(300));

        var dropped = batch.EnqueueAsync([engine.Repo.Full("Lib/Calc.cs")], null, gone.Token);
        var staying = batch.EnqueueAsync([engine.Repo.Full("Lib/Greeting.cs")], null, CancellationToken.None);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await gone.CancelAsync();

        var report = await staying;
        Assert.Equal(1, batch.LastBatchSize);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dropped);
        // The client that stayed is answered for its own file only, not for the one that left.
        Assert.DoesNotContain(report.Introduced, d => d.Path == "App/Program.cs" && d.Message.Contains("Plus", StringComparison.Ordinal));
        Assert.NotEmpty(report.Projects);
    }

    [Fact]
    public async Task A_batch_with_a_full_check_answers_the_full_check_too()
    {
        await using var engine = await EngineHarness.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var batch = new CheckBatch(engine.Checker, TimeSpan.FromMilliseconds(150));

        var scoped = batch.EnqueueAsync([engine.Repo.Full("Lib/Greeting.cs")], null, TestContext.Current.CancellationToken);
        var full = batch.EnqueueAsync(null, null, TestContext.Current.CancellationToken);
        var (scopedReport, fullReport) = (await scoped, await full);

        Assert.Equal(2, batch.LastBatchSize);
        // The full check sees the break in a file it did not name; the scoped one does not.
        Assert.Contains(fullReport.Introduced, d => d.Path == "App/Program.cs");
        Assert.DoesNotContain(scopedReport.Introduced, d => d.Path == "App/Program.cs");
    }

    private static string Render(CheckReport report) => CheckOperation.Render(report).Text;
}
