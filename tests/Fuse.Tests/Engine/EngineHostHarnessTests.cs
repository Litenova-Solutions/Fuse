using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The engine's request loop driven in this process, so what a test sees is what an engine answers: concurrent
///     requests each get their own answer, an engine error comes back as that error rather than a dropped connection,
///     and every check is named and timed in the log.
/// </summary>
public class EngineHostHarnessTests
{
    [Fact]
    public async Task Two_concurrent_checks_both_answer()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await EngineHostHarness.StartAsync(repo);

        var answers = await Task.WhenAll(engine.CheckAsync(repo.Full("Lib/Calc.cs")), engine.CheckAsync(repo.Full("Lib/Greeting.cs")));

        Assert.Equal(2, answers.Length);
        Assert.All(answers, a => Assert.Equal(ResponseStatus.Ok, a.Status));
        Assert.All(answers, a => Assert.NotNull(a.Check));
    }

    [Fact]
    public async Task Two_concurrent_checks_with_a_break_each_both_see_their_own()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await EngineHostHarness.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        repo.Replace("Lib/Formatter.cs", "Format(", "Format2(");

        var calc = repo.Full("Lib/Calc.cs").Replace('\\', '/');
        var formatter = repo.Full("Lib/Formatter.cs").Replace('\\', '/');
        var answers = await Task.WhenAll(engine.CheckAsync(calc), engine.CheckAsync(formatter));

        Assert.Contains(answers, a => a.Check!.Introduced.Any(d => d.Message.Contains("'Add'", StringComparison.Ordinal)));
        Assert.Contains(answers, a => a.Check!.Introduced.Any(d => d.Message.Contains("'Format'", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_unrestored_project_is_answered_as_restore_needed()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await EngineHostHarness.StartAsync(repo);
        File.Delete(repo.Full("Lib/obj/project.assets.json"));
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");

        var answer = await engine.CheckAsync(repo.Full("Lib/Calc.cs"));

        // The hook reports this code, and only this code, as something the agent has to act on.
        Assert.Equal(ResponseStatus.Error, answer.Status);
        Assert.Equal(ErrorCode.RestoreNeeded, answer.Error);
        Assert.Contains("dotnet restore", answer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_log_names_its_request_and_its_wait_for_the_gate()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await EngineHostHarness.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");

        await engine.SendAsync(new EngineRequest("", RequestKind.Check, RequestId: "t-1", Files: [repo.Full("Lib/Calc.cs")]));

        var line = engine.Log.Split('\n').Select(l => l[(l.IndexOf("phases ", StringComparison.Ordinal) is var i and >= 0 ? i : 0)..]).Single(l => l.StartsWith("phases id=t-1 ", StringComparison.Ordinal));
        Assert.True(PhaseLine.TryParse(line.TrimEnd(), out _, out var kind, out var phases));
        Assert.Equal("Check", kind);
        Assert.Contains("gate", phases.Keys);
        Assert.Contains("total", phases.Keys);
    }

    [Fact]
    public async Task A_check_starts_the_background_load_of_dependents()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await EngineHostHarness.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");

        await engine.CheckAsync(repo.Full("Lib/Calc.cs"));

        // A body edit loads only Lib; the dependents of the changed project load in the background after the answer.
        var loaded = false;
        for (var i = 0; i < 300 && !loaded; i++)
        {
            loaded = engine.Log.Split('\n').Any(l => l.Contains(" loaded ", StringComparison.Ordinal) && !l.Contains(" loaded Lib ", StringComparison.Ordinal));
            if (!loaded)
                await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.True(loaded, $"no dependent was preloaded after the check; log:\n{engine.Log}");
    }
}
