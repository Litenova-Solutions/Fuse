using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A session's credit for a file it wrote, and what may drop it: only a sync that verifies the file matches
///     HEAD again. A credit recorded by a check the result store answered, before any sync has folded the file
///     into a generation, survives a read of the writers from that older generation, as the background re-check
///     reads them, and a later session is still not told that file's errors.
/// </summary>
public class SessionCreditTests
{
    private const string AddMissing = "'Calc' does not contain a definition for 'Add'";
    private const string GreetMissing = "'IGreeter' does not contain a definition for 'Greet'";

    [Fact]
    public async Task A_credit_recorded_after_a_generation_was_pinned_survives_a_read_of_that_generation()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var pinned = engine.Workspace.PinGeneration();
        engine.Repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        engine.Repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
        // Recorded as a kept answer records it: after the pin, before any sync folds the file in.
        engine.Sessions.Record("claude:smoke-a", [engine.Repo.PathOf("Lib/Calc.cs")]);

        // The background re-check reads the writers from the newest generation without a sync.
        _ = engine.Sessions.WritersOf(pinned.Changed);

        await engine.SyncAsync("Lib/Calc.cs", "Lib/Greeting.cs");
        var answer = await engine.CheckAllAsSessionAsync("smoke-env");

        Assert.DoesNotContain(answer.Errors, e => e.Error.Message.Contains(AddMissing, StringComparison.Ordinal));
        Assert.Contains(answer.Errors, e => e.Error.Message.Contains(GreetMissing, StringComparison.Ordinal));
        Assert.True(answer.LeftToOtherSessions > 0);
    }

    [Fact]
    public async Task A_credit_from_an_answer_kept_by_the_result_store_survives_the_background_read()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var first = await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { Session = "claude:smoke-a" });
        Assert.IsType<EngineResponse.CheckAnswered>(first);

        // The file matches HEAD again, so a check of every change drops the credit, as it should.
        repo.Replace("Lib/Calc.cs", "public int Plus(", "public int Add(");
        var clean = await engine.SendAsync(new EngineRequest.CheckChanges(WaitForLoad: true));
        Assert.Empty(Assert.IsType<EngineResponse.CheckAnswered>(clean).Report.Errors);
        // A status answer syncs the clean tree and schedules the background re-check, so its read is still
        // to come when the same error is written again below.
        Assert.IsType<EngineResponse.StatusAnswered>(await engine.SendAsync(new EngineRequest.Status()));

        // The same edit again: the result store answers both checks of the post-edit hook, so no sync folds
        // Lib/Calc.cs into a generation, but the session is credited with it again.
        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        var kept = await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { Session = "claude:smoke-a" });
        var confirmed = await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { Session = "claude:smoke-a" });
        Assert.IsType<EngineResponse.CheckAnswered>(kept);
        Assert.IsType<EngineResponse.CheckAnswered>(confirmed);
        Assert.Contains(engine.Log.Split('\n'), l => l.Contains("CheckFiles Calc.cs took 0 ms", StringComparison.Ordinal));

        // The background read of the writers runs from the generation pinned before the credit.
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
        var other = await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Greeting.cs")], WaitForLoad: true) { Session = "claude:smoke-b" });
        Assert.IsType<EngineResponse.CheckAnswered>(other);
        var named = await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Greeting.cs")], WaitForLoad: true) { Session = "smoke-env" });
        Assert.IsType<EngineResponse.CheckAnswered>(named);

        var all = await engine.SendAsync(new EngineRequest.CheckChanges(WaitForLoad: true) { Session = "smoke-env" });
        var report = Assert.IsType<EngineResponse.CheckAnswered>(all).Report;
        Assert.DoesNotContain(report.Errors, e => e.Error.Message.Contains(AddMissing, StringComparison.Ordinal));
        Assert.Contains(report.Errors, e => e.Error.Message.Contains(GreetMissing, StringComparison.Ordinal));
        Assert.True(report.LeftToOtherSessions > 0);
        Assert.Contains("Lib/Calc.cs\tclaude:smoke-a", File.ReadAllLines(Path.Combine(repo.Root.StateDirectory, "sessions.tsv")));
    }
}
