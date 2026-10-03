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
    public async Task A_check_that_names_only_files_it_cannot_check_is_answered_as_invalid_path_not_as_no_errors()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        var missing = Assert.IsType<EngineResponse.Unanswered>(await engine.CheckAsync("does/not/exist.cs", "README.md"));

        Assert.Equal(ErrorCode.InvalidPath, missing.Code);
        Assert.Contains("does/not/exist.cs, README.md", missing.Message, StringComparison.Ordinal);
        // One C# source among them is enough to check it and answer.
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync("does/not/exist.cs", "Lib/Calc.cs"));
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
    public async Task A_check_log_names_its_request_and_its_wait_for_the_request_lock()
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
    public async Task A_check_sent_while_a_test_plan_emits_is_answered_before_the_plan_finishes()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(2));

        // Holding the preparation keeps the plan in its shadow preparation, after its selection, for as long as the test likes.
        await engine.Planner.Preparation.WaitAsync(limit.Token);
        Task<EngineResponse> plan;
        try
        {
            plan = engine.SendAsync(new EngineRequest.PlanAffectedTests { RequestId = "p-1" }, limit.Token);
            await WaitForLogAsync(engine, "test plan: selection in ");

            var check = await engine.SendAsync(new EngineRequest.CheckFiles(["Lib/Calc.cs"], WaitForLoad: true) { RequestId = "c-1" }, limit.Token);

            Assert.IsType<EngineResponse.CheckAnswered>(check);
            Assert.False(plan.IsCompleted, "the plan finished before the check, so the check may have waited for it");
        }
        finally
        {
            engine.Planner.Preparation.Release();
        }

        var answered = Assert.IsType<EngineResponse.PlanAnswered>(await plan);
        Assert.IsType<TestRunMode.Shadow>(Assert.Single(answered.Plan.Runs).Mode);
        var (kind, phases) = PhaseLineOf(engine, "p-1");
        Assert.Equal("PlanAffectedTests", kind);
        Assert.Equal(["emit", "gate", "mirror", "selection", "sync", "total"], phases.Keys.Order(StringComparer.Ordinal));
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
    public async Task A_file_named_in_another_letter_case_is_the_file_and_its_errors_at_head_are_not_introduced()
    {
        // Only a file system that ignores case finds the file under the other spelling.
        if (!OperatingSystem.IsWindows())
            return;
        using var repo = FixtureRepo.CreateStandard();
        repo.Replace("Lib/Calc.cs", "a * b;", "a * existingError;");
        repo.Commit("an error at HEAD");
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        var named = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync("lib/calc.cs")).Report;
        var every = Assert.IsType<EngineResponse.CheckAnswered>(await engine.CheckAsync()).Report;

        Assert.Empty(named.Errors);
        Assert.Equal(1, named.FilesChecked);
        Assert.Empty(every.Errors);
    }

    [Fact]
    public async Task A_check_that_also_names_a_file_outside_the_repository_still_reports_the_edit_inside_it()
    {
        using var repo = FixtureRepo.CreateStandard();
        var outside = Path.Combine(repo.Root.Path + "-outside", "Linked.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "public class Linked {}\n");
        try
        {
            await using var engine = await InProcessRequestRouter.StartAsync(repo);
            repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");

            var answer = await engine.CheckAsync(outside, "Lib/Calc.cs");

            var report = Assert.IsType<EngineResponse.CheckAnswered>(answer).Report;
            Assert.Equal("CS0103", Assert.Single(report.Errors).Error.Id);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(outside)!, recursive: true);
        }
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

    [Fact]
    public async Task A_check_from_a_session_is_recorded_and_answered_to_that_session()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");

        await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Greeting.cs")], WaitForLoad: true) { Session = "claude:b" });
        var answer = await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { Session = "claude:a", RequestId = "attributed" });
        var unattributed = await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true));

        var report = Assert.IsType<EngineResponse.CheckAnswered>(answer).Report;
        Assert.DoesNotContain(report.Errors, e => e.Error.Message.Contains("'Greet'", StringComparison.Ordinal));
        Assert.Equal(1, report.LeftToOtherSessions);
        var all = Assert.IsType<EngineResponse.CheckAnswered>(unattributed).Report;
        Assert.Contains(all.Errors, e => e.Error.Message.Contains("'Greet'", StringComparison.Ordinal));
        Assert.Equal(0, all.LeftToOtherSessions);

        var recorded = File.ReadAllLines(Path.Combine(repo.Root.StateDirectory, "sessions.tsv"));
        Assert.Equal(["Lib/Calc.cs\tclaude:a", "Lib/Greeting.cs\tclaude:b"], recorded);
        Assert.Contains(engine.Log.Split('\n'), l => l.Contains("phases id=attributed ", StringComparison.Ordinal) && l.Contains(" attribution=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_starting_engine_forgets_a_file_that_matches_HEAD_before_it_is_written_again()
    {
        using var repo = FixtureRepo.CreateStandard();
        // What an earlier engine left: session b credited with Lib/Greeting.cs, which has since been reverted.
        Directory.CreateDirectory(repo.Root.StateDirectory);
        var record = Path.Combine(repo.Root.StateDirectory, "sessions.tsv");
        File.WriteAllLines(record, ["Lib/Greeting.cs\tclaude:b"]);
        await using var engine = await InProcessRequestRouter.StartAsync(repo);

        // Session a now writes the file before any check runs.
        repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
        await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { Session = "claude:b" });
        var answer = await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Greeting.cs")], WaitForLoad: true) { Session = "claude:a" });

        // Lib/Greeting.cs is a's alone, so the Add error in App/Program.cs, which b caused, is left out of a's answer.
        var report = Assert.IsType<EngineResponse.CheckAnswered>(answer).Report;
        Assert.DoesNotContain(report.Errors, e => e.Error.Message.Contains("'Add'", StringComparison.Ordinal));
        Assert.Equal(1, report.LeftToOtherSessions);
        Assert.Equal(["Lib/Calc.cs\tclaude:b", "Lib/Greeting.cs\tclaude:a"], File.ReadAllLines(record));
    }

    [Fact]
    public async Task A_session_id_that_cannot_be_recorded_is_answered_as_no_session()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");

        var answer = await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true) { Session = "a\tb" });

        Assert.NotEmpty(Assert.IsType<EngineResponse.CheckAnswered>(answer).Report.Errors);
        Assert.False(File.Exists(Path.Combine(repo.Root.StateDirectory, "sessions.tsv")));
    }

    /// <summary>Waits up to a minute for the router's log to hold <paramref name="text"/>, which a background load writes when it gets there.</summary>
    private static async Task WaitForLogAsync(InProcessRequestRouter engine, string text)
    {
        for (var i = 0; i < 600 && !engine.Log.Contains(text, StringComparison.Ordinal); i++)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(engine.Log.Contains(text, StringComparison.Ordinal), $"the log never held \"{text}\":\n{engine.Log}");
    }
}
