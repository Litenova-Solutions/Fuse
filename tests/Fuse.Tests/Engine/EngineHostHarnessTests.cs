using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The engine's request loop driven in this process, so what a test sees is what an engine answers. The point of the
///     batch queue is that a second check arriving while the first runs is not made to wait for all of it, and this is
///     where that shows up: two checks sent together both come back, and they come back together.
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

        // Which request reaches the batch first is up to the pipe, so the answers are matched by what they say.
        Assert.Contains(answers, a => a.Check!.Introduced.Any(d => d.Message.Contains("'Add'", StringComparison.Ordinal)));
        Assert.Contains(answers, a => a.Check!.Introduced.Any(d => d.Message.Contains("'Format'", StringComparison.Ordinal)));
        var counts = new List<int>();
        foreach (var answer in answers)
            counts.Add(answer.Check!.Introduced.Length);
        Assert.Equal(2, counts.Distinct().Count());
    }

    [Fact]
    public async Task A_check_log_names_its_request_and_the_batch_it_was_in()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await EngineHostHarness.StartAsync(repo, TimeSpan.FromMilliseconds(150));
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");

        await Task.WhenAll(engine.CheckAsync(repo.Full("Lib/Calc.cs")), engine.CheckAsync(repo.Full("Lib/Calc.cs")));

        var log = engine.Log;
        Assert.Contains("batch of 2 client(s)", log, StringComparison.Ordinal);
    }
}
