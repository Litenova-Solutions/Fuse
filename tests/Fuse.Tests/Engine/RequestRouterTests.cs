using Fuse.Failures;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The engine's request loop driven in this process, so what a test sees is what an engine answers: concurrent
///     requests each get their own answer, an engine error comes back as that error rather than a dropped connection,
///     and every check is named and timed in the log.
/// </summary>
public class RequestRouterTests
{
    [Fact]
    public async Task Two_concurrent_checks_both_answer()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        var answers = await Task.WhenAll(engine.CheckAsync(repo.Full("Lib/Calc.cs")), engine.CheckAsync(repo.Full("Lib/Greeting.cs")));

        Assert.Equal(2, answers.Length);
        Assert.All(answers, a => Assert.IsType<EngineResponse.CheckAnswered>(a));
    }

    [Fact]
    public async Task Two_concurrent_checks_with_a_break_each_both_see_their_own()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        repo.Replace("Lib/Formatter.cs", "Format(", "Format2(");

        var calc = repo.Full("Lib/Calc.cs").Replace('\\', '/');
        var formatter = repo.Full("Lib/Formatter.cs").Replace('\\', '/');
        var answers = await Task.WhenAll(engine.CheckAsync(calc), engine.CheckAsync(formatter));

        var reports = answers.Select(a => Assert.IsType<EngineResponse.CheckAnswered>(a).Report).ToList();
        Assert.Contains(reports, r => r.Errors.Any(e => e.Error.Message.Contains("'Add'", StringComparison.Ordinal)));
        Assert.Contains(reports, r => r.Errors.Any(e => e.Error.Message.Contains("'Format'", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task An_unrestored_project_is_answered_as_restore_needed()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        File.Delete(repo.Full("Lib/obj/project.assets.json"));
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");

        var answer = await engine.CheckAsync(repo.Full("Lib/Calc.cs"));

        // The hook reports this code, and only this code, as something the agent has to act on.
        var unanswered = Assert.IsType<EngineResponse.Unanswered>(answer);
        Assert.Equal(ErrorCode.RestoreNeeded, unanswered.Code);
        Assert.Contains("dotnet restore", unanswered.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_that_names_an_empty_or_invalid_path_is_answered_as_invalid_path()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        var empty = Assert.IsType<EngineResponse.Unanswered>(await engine.CheckAsync(repo.Full("Lib/Calc.cs"), " "));
        var invalid = Assert.IsType<EngineResponse.Unanswered>(await engine.CheckAsync("Lib/Ca\0lc.cs"));

        // The request is refused as a whole, with the name that is wrong, rather than checked without it.
        Assert.Equal(ErrorCode.InvalidPath, empty.Code);
        Assert.Contains("empty", empty.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCode.InvalidPath, invalid.Code);
        Assert.Contains("\"Lib/Ca\0lc.cs\"", invalid.Message, StringComparison.Ordinal);
        // A relative name is a path like any other.
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync("Lib/Calc.cs"));
    }

    [Fact]
    public async Task A_check_log_names_its_request_and_its_wait_for_the_gate()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");

        await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { RequestId = "t-1" });

        var (kind, phases) = PhaseLineOf(engine, "t-1");
        Assert.Equal("CheckFiles", kind);
        Assert.Contains("gate", phases.Keys);
        Assert.Contains("total", phases.Keys);
        Assert.Contains(engine.Log.Split('\n'), l => l.Contains("CheckFiles Calc.cs took ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_check_of_every_change_is_logged_by_its_case()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(new EngineRequest.CheckChanges(WaitForLoad: true) { RequestId = "t-2" }));

        Assert.Equal("CheckChanges", PhaseLineOf(engine, "t-2").Kind);
        Assert.Contains(engine.Log.Split('\n'), l => l.Contains("CheckChanges took ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plan_of_every_test_is_answered_with_its_plan_and_logged_by_its_case()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        var answer = await engine.SendAsync(new EngineRequest.PlanAllTests { RequestId = "t-3" });

        var plan = Assert.IsType<EngineResponse.PlanAnswered>(answer).Plan;
        Assert.NotEmpty(plan.Runs);
        Assert.All(plan.Runs, r => Assert.IsType<TestRunMode.Build>(r.Mode));
        Assert.Equal("PlanAllTests", PhaseLineOf(engine, "t-3").Kind);
    }

    [Fact]
    public async Task A_check_read_from_a_line_without_its_optional_fields_is_answered_and_the_next_one_is_too()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(2));

        // No request id and no file list: the request log has nothing to name, and the request lock must still be released.
        var bare = ProtocolJson.ReadRequest("""{"request":"CheckFiles","waitForLoad":true}""")!;
        var first = await engine.SendAsync(bare, limit.Token);
        var second = await engine.SendAsync(new EngineRequest.CheckFiles(["Lib/Calc.cs"], WaitForLoad: true), limit.Token);

        Assert.Equal(0, Assert.IsType<EngineResponse.CheckAnswered>(first).Report.FilesChecked);
        Assert.IsType<EngineResponse.CheckAnswered>(second);
        Assert.Contains(engine.Log.Split('\n'), l => l.Contains(" CheckFiles took ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ping_and_a_shutdown_are_acknowledged()
    {
        await using var engine = await InProcessRequestRouter.StartAsync();

        Assert.IsType<EngineResponse.Acknowledged>(await engine.SendAsync(new EngineRequest.Ping()));
        Assert.IsType<EngineResponse.Acknowledged>(await engine.SendAsync(new EngineRequest.ShutDown()));
    }

    /// <summary>The kind and the phases of the phase line the router wrote for <paramref name="requestId"/>.</summary>
    private static (string Kind, Dictionary<string, double> Phases) PhaseLineOf(InProcessRequestRouter engine, string requestId)
    {
        var line = engine.Log.Split('\n').Select(l => l[(l.IndexOf("phases ", StringComparison.Ordinal) is var i and >= 0 ? i : 0)..]).Single(l => l.StartsWith($"phases id={requestId} ", StringComparison.Ordinal));
        Assert.True(PhaseLine.TryParse(line.TrimEnd(), out _, out var kind, out var phases));
        return (kind, phases);
    }

    [Fact]
    public async Task A_check_starts_the_background_load_of_dependents()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
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

    [Fact]
    public async Task A_dependent_that_failed_to_preload_is_loaded_in_the_background_once_it_is_restored()
    {
        using var repo = FixtureRepo.CreateStandard();
        var assets = repo.Full("App/obj/project.assets.json");
        var restored = File.ReadAllBytes(assets);
        File.Delete(assets);
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");

        await engine.CheckAsync(repo.Full("Lib/Calc.cs"));
        await WaitForLogAsync(engine, "preload of App skipped: restore needed");
        Assert.DoesNotContain(" loaded App in ", engine.Log, StringComparison.Ordinal);

        // What `dotnet restore App` writes; the next request starts the background load again.
        File.WriteAllBytes(assets, restored);
        await engine.CheckAsync(repo.Full("Lib/Calc.cs"));
        await WaitForLogAsync(engine, " loaded App in ");
    }

    /// <summary>Waits up to a minute for the router's log to hold <paramref name="text"/>, which a background load writes when it gets there.</summary>
    private static async Task WaitForLogAsync(InProcessRequestRouter engine, string text)
    {
        for (var i = 0; i < 600 && !engine.Log.Contains(text, StringComparison.Ordinal); i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(engine.Log.Contains(text, StringComparison.Ordinal), $"the log never held \"{text}\":\n{engine.Log}");
    }
}
