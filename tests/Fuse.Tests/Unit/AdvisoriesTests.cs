using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Fuse.Check.Model;
using Fuse.Dotnet.Test;
using Fuse.Engine.Routing;
using Fuse.Operations;
using Fuse.Operations.Check;
using Fuse.Operations.Control;
using Fuse.Operations.Test.Rendering;
using Fuse.Paths;
using Fuse.Protocol.Analysis;
using Fuse.Protocol.Check;
using Fuse.Protocol.Testing;
using Fuse.Protocol.Transport;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     The one line a command prints when a decided limit or a slow repository practice applies. A healthy report
///     prints none, each condition prints its exact line, two conditions keep only the higher one on check and gate
///     and both on status, and hooks never print one.
/// </summary>
public class AdvisoriesTests
{
    private const string Stamp = "; tree 9f2c41aa07b3 over 3 changed file(s)";

    private static CheckReport Healthy => new([], 3, [], ["Lib"], 2, false, 0, 0, 0);

    [Fact]
    public void A_healthy_report_prints_no_note()
    {
        var result = CheckOperation.Render(Healthy, treeStamp: Stamp);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("fuse: note:", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Uncommitted_files_past_the_kept_answer_limit_name_the_count()
    {
        var report = Healthy with { TreeFiles = 512 };
        var result = CheckOperation.Render(report, treeStamp: Stamp);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "fuse: note: 512 uncommitted files since HEAD (past 500, kept-answer caching is skipped); commit completed work to keep checks fast",
            result.Text.Split('\n')[^1]);
    }

    [Fact]
    public void Live_sessions_without_a_session_name_how_to_isolate()
    {
        var report = Healthy with { LiveSessions = ["judge", "worker-1"] };
        var result = CheckOperation.Render(report, treeStamp: Stamp);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "fuse: note: 2 session(s) are active (judge, worker-1); pass --session <name> or set FUSE_SESSION=judge to isolate results",
            result.Text.Split('\n')[^1]);
    }

    [Fact]
    public void Unfinished_analyzers_name_their_budget()
    {
        var report = Healthy with { AnalyzersUnfinished = 2 };
        var result = CheckOperation.Render(report, treeStamp: Stamp);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "fuse: note: 2 analyzer(s) did not finish within the 60s budget; check their CompilationStart and AdditionalFiles cost",
            result.Text.Split('\n')[^1]);
    }

    [Fact]
    public void A_large_target_file_names_its_lines()
    {
        var report = Healthy with { LargestTargetFile = "src/Big.cs", LargestTargetLines = 1600 };
        var result = CheckOperation.Render(report, treeStamp: Stamp);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "fuse: note: src/Big.cs is 1600 lines; splitting large files keeps Roslyn binding and edits fast",
            result.Text.Split('\n')[^1]);
    }

    [Fact]
    public void An_error_answer_keeps_its_exit_code_with_a_note()
    {
        ReportedError[] errors = [new(new CompilerError("App/Program.cs", 2, 28, "CS1061", "no Add"))];
        var report = new CheckReport(errors, 3, ["App"], [], 0, false, 1, 1, 0) { AnalyzersUnfinished = 1 };

        var result = CheckOperation.Render(report, treeStamp: Stamp);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            "fuse: note: 1 analyzer(s) did not finish within the 60s budget; check their CompilationStart and AdditionalFiles cost",
            result.Text.Split('\n')[^1]);
    }

    [Fact]
    public void Two_conditions_keep_only_the_higher_note_on_check()
    {
        var report = Healthy with { LiveSessions = ["judge"], AnalyzersUnfinished = 2, LargestTargetFile = "src/Big.cs", LargestTargetLines = 1800 };

        var result = CheckOperation.Render(report, treeStamp: Stamp);

        var notes = result.Text.Split('\n').Where(line => line.StartsWith("fuse: note:", StringComparison.Ordinal)).ToList();
        Assert.Single(notes);
        Assert.Contains("session(s) are active", notes[0], StringComparison.Ordinal);
    }

    [Fact]
    public void An_unstamped_answer_prints_no_note_as_hooks_read_it()
    {
        var report = Healthy with { LiveSessions = ["judge"], AnalyzersUnfinished = 2, LargestTargetFile = "src/Big.cs", LargestTargetLines = 1800, TreeFiles = 512 };

        var result = CheckOperation.Render(report);

        Assert.DoesNotContain("fuse: note:", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shadow_skip_names_the_file_and_the_faster_read()
    {
        var plan = new TestPlan(
            [new TestRun("/repo/Lib.Tests/Lib.Tests.csproj", "Lib.Tests", new TestRunMode.Build(), null, false, null, [], [])
            {
                ShadowSkipReason = "walks parent directories from AppContext.BaseDirectory or Assembly.Location in ShadowRoot.cs",
            }],
            4,
            40,
            "ran 4 test(s) affected by your changes out of 40; fuse test --all runs everything");

        var notes = Advisories.TestNotes(plan);

        Assert.Equal(
            ["fuse: note: shadow run skipped for Lib.Tests (ShadowRoot.cs walks parent directories from AppContext.BaseDirectory); use [CallerFilePath] for faster test runs"],
            notes);
    }

    [Fact]
    public void A_test_answer_prints_the_shadow_note_last()
    {
        var plan = new TestPlan(
            [new TestRun("/repo/Lib.Tests/Lib.Tests.csproj", "Lib.Tests", new TestRunMode.Build(), null, false, null, [], [])
            {
                ShadowSkipReason = "walks parent directories from AppContext.BaseDirectory or Assembly.Location in ShadowRoot.cs",
            }],
            4,
            40,
            "ran 4 test(s) affected by your changes out of 40; fuse test --all runs everything");

        var result = TestResultRenderer.Render(new TestRunSummary(4, 0, 0, []), plan.Summary, 1.5, plan: plan);

        Assert.Equal(Outcome.Clean, result.Outcome);
        Assert.Equal(
            "fuse: note: shadow run skipped for Lib.Tests (ShadowRoot.cs walks parent directories from AppContext.BaseDirectory); use [CallerFilePath] for faster test runs",
            result.Text.Split('\n')[^1]);
    }

    [Fact]
    public void A_shadow_run_plans_no_note()
    {
        var plan = new TestPlan(
            [new TestRun("/repo/Lib.Tests/Lib.Tests.csproj", "Lib.Tests", new TestRunMode.Shadow("/shadow/Lib.Tests.dll"), null, false, null, [], [])],
            4,
            40,
            "summary");

        Assert.Empty(Advisories.TestNotes(plan));
    }

    [Fact]
    public void A_plan_maps_its_shadow_skip_reason_field_by_field()
    {
        var root = FixtureRepo.CheckoutRoot;
        var planned = new PlannedRun(root.PathOf("Lib.Tests/Lib.Tests.csproj"), "Lib.Tests", new RunMode.Build(), null, new TestRunnerPlan.VsTest(null))
        {
            ShadowSkipReason = "walks parent directories from AppContext.BaseDirectory or Assembly.Location in ShadowRoot.cs",
        };
        var result = new TestPlanResult([planned], 4, 40, "summary");

        var plan = ResponseMapper.Plan(result);

        Assert.Equal(
            "walks parent directories from AppContext.BaseDirectory or Assembly.Location in ShadowRoot.cs",
            Assert.Single(plan.Runs).ShadowSkipReason);
    }

    [Fact]
    public void A_shadow_skip_reason_survives_the_pipe()
    {
        var root = FixtureRepo.CheckoutRoot;
        var planned = new PlannedRun(root.PathOf("Lib.Tests/Lib.Tests.csproj"), "Lib.Tests", new RunMode.Build(), null, new TestRunnerPlan.VsTest(null))
        {
            ShadowSkipReason = "walks parent directories from AppContext.BaseDirectory or Assembly.Location in ShadowRoot.cs",
        };

        var line = ProtocolJson.Serialize(ResponseMapper.Answered(new TestPlanResult([planned], 4, 40, "summary")));
        var plan = Assert.IsType<EngineResponse.PlanAnswered>(ProtocolJson.ReadResponse(line)).Plan;

        Assert.Contains("\"shadowSkipReason\":\"walks parent directories", line, StringComparison.Ordinal);
        Assert.Equal(planned.ShadowSkipReason, Assert.Single(plan.Runs).ShadowSkipReason);
    }

    [Fact]
    public void A_line_without_a_shadow_skip_reason_reads_none()
    {
        var plan = Assert.IsType<EngineResponse.PlanAnswered>(ProtocolJson.ReadResponse(
            """{"status":"PlanAnswered","plan":{"runs":[{"project":"/repo/Lib.Tests.csproj","name":"Lib.Tests","mode":{"kind":"Build"},"filter":null,"usesTestingPlatform":false,"filterArguments":[],"targetPaths":[]}],"selectedTests":0,"totalTests":40,"summary":"summary"}}""")).Plan;

        Assert.Null(Assert.Single(plan.Runs).ShadowSkipReason);
    }

    [Theory]
    [InlineData("src/Big.cs", true)]
    [InlineData("src/Foo.g.cs", false)]
    [InlineData("src/Foo.Designer.cs", false)]
    [InlineData("src/obj/Big.cs", false)]
    [InlineData("src/bin/Debug/Big.cs", false)]
    [InlineData("src/Migrations/001_AddTable.cs", false)]
    [InlineData("src/Notes.md", false)]
    [InlineData("src/App.razor", false)]
    public void Only_a_changed_non_generated_csharp_file_may_name_a_note(string relative, bool expected) =>
        Assert.Equal(expected, CheckRoute.IsLargeFileCandidate(relative));

    [Fact]
    public void A_generated_file_of_1600_lines_names_no_note()
    {
        var generated = new[] { ("src/Foo.g.cs", 1600), ("src/Small.cs", 100) }
            .Where(candidate => CheckRoute.IsLargeFileCandidate(candidate.Item1));
        Assert.Null(CheckRoute.LargestLargeFile(generated));
        var largest = CheckRoute.LargestLargeFile([("src/Small.cs", 100), ("src/Big.cs", 2000)]);
        Assert.Equal("src/Big.cs", largest?.Relative);
        Assert.Equal(2000, largest?.Lines);
        Assert.Null(CheckRoute.LargestLargeFile([("src/Small.cs", 1499)]));
    }

    [Fact]
    public void The_large_file_limit_is_one_number_in_both_layers() =>
        Assert.Equal(Advisories.LargeFileLines, CheckRoute.LargeFileLines);

    [Fact]
    public void A_status_with_two_conditions_lists_both_notes_in_order()
    {
        var now = DateTimeOffset.UtcNow;
        var status = new EngineStatus(123, 100, 512, "0123456789abcdef0123456789abcdef01234567", "9f2c41aa07b3", [new ReportedSession("judge", ["src/**"], null, now.AddMinutes(30))]);

        var notes = Advisories.StatusNotes(status, now);

        Assert.Equal(
            [
                "fuse: note: 512 uncommitted files since HEAD (past 500, kept-answer caching is skipped); commit completed work to keep checks fast",
                "fuse: note: session(s) judge have been open for over 2 hours; run 'fuse session end <name>' if finished",
            ],
            notes);
    }

    [Fact]
    public void A_recently_opened_session_names_no_note()
    {
        var now = DateTimeOffset.UtcNow;
        var status = new EngineStatus(123, 100, 3, "0123456789abcdef0123456789abcdef01234567", "9f2c41aa07b3", [new ReportedSession("judge", ["src/**"], null, now.AddMinutes(150))]);

        Assert.Empty(Advisories.StatusNotes(status, now));
    }

    [Fact]
    public async Task A_status_names_a_session_open_for_over_two_hours()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        Directory.CreateDirectory(repo.Root.StateDirectory);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(30);
        await File.WriteAllTextAsync(
            Path.Combine(repo.Root.StateDirectory, "registered-sessions.tsv"),
            $"{expiry.UtcTicks}\tjudge\tsrc/**\n",
            TestContext.Current.CancellationToken);

        var result = await StatusOperation.RunAsync(repo.Root, false, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(
            "fuse: note: session(s) judge have been open for over 2 hours; run 'fuse session end <name>' if finished",
            result.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_status_answer_without_notes_carries_an_empty_list()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });

        var result = await StatusOperation.RunAsync(repo.Root, true, TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(result.Text);
        Assert.Empty(document.RootElement.GetProperty("advisories").EnumerateArray().ToList());
    }

    [Fact]
    public async Task A_gate_keeps_only_the_higher_note_in_text()
    {
        var report = Healthy with { Tree = "9f2c41aa07b3", TreeFiles = 3, LiveSessions = ["judge"], AnalyzersUnfinished = 2 };
        var line = ProtocolJson.Serialize(new EngineResponse.CheckAnswered(report));
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var engine = new FakeEngine(repo.Root, line);

        var result = await GateOperation.RunAsync(repo.Root, repo.Path, [], null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var notes = result.Text.Split('\n').Where(text => text.StartsWith("fuse: note:", StringComparison.Ordinal)).ToList();
        Assert.Single(notes);
        Assert.Equal(
            "fuse: note: 1 session(s) are active (judge); pass --session <name> or set FUSE_SESSION=judge to isolate results",
            notes[0]);
    }

    [Fact]
    public async Task A_gate_answer_carries_its_note_in_json()
    {
        var report = Healthy with { Tree = "9f2c41aa07b3", TreeFiles = 3, LiveSessions = ["judge"] };
        var line = ProtocolJson.Serialize(new EngineResponse.CheckAnswered(report));
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        using var engine = new FakeEngine(repo.Root, line);

        var result = await GateOperation.RunAsync(repo.Root, repo.Path, ["--json"], null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        using var json = JsonDocument.Parse(result.Text);
        Assert.Equal(
            "fuse: note: 1 session(s) are active (judge); pass --session <name> or set FUSE_SESSION=judge to isolate results",
            Assert.Single(json.RootElement.GetProperty("advisories").EnumerateArray()).GetString());
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
