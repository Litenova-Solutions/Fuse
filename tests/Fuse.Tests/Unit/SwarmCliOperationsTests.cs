using System.Text;
using System.Text.Json;
using Fuse.Check.Model;
using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class SwarmCliOperationsTests
{
    [Fact]
    public void New_protocol_requests_and_worktree_root_round_trip_with_aot_json()
    {
        EngineRequest[] requests =
        [
            new EngineRequest.YieldMemory { WorktreeRoot = @"C:\wt1" },
            new EngineRequest.Judge(new JudgeRequest(
                JudgeRequest.JudgeMode.Race,
                [new JudgeRequest.Candidate("cand1", "--- a/A.cs\n+++ b/A.cs\n", @"C:\wt1")],
                RunTests: true,
                MaxFailures: 5))
            { WorktreeRoot = @"C:\wt1" },
            new EngineRequest.Graph { WorktreeRoot = @"C:\wt1" },
            new EngineRequest.Impact(["Lib/Calc.cs"]) { WorktreeRoot = @"C:\wt1" },
            new EngineRequest.SessionGuard(new FreezeGuardRequest("contracts", Unfreeze: false, ["src/Contracts/**"])) { WorktreeRoot = @"C:\wt1" },
            new EngineRequest.WatchStream { Session = "w1", All = true, WorktreeRoot = @"C:\wt1" },
            new EngineRequest.CheckFiles(["Lib/Calc.cs"], WaitForLoad: false) { Brief = true, InteractiveFast = true, WorktreeRoot = @"C:\wt1" },
            new EngineRequest.PlanAffectedTests { Clean = true, NoCache = true, MaxFailures = 5, WorktreeRoot = @"C:\wt1" },
        ];

        foreach (var req in requests.Select(r => r with { BuildId = "7.0.0/abc", RequestId = "1-1" }))
        {
            var json = ProtocolJson.Serialize(req);
            Assert.StartsWith($"{{\"request\":\"{req.GetType().Name}\",", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"kind\"", json, StringComparison.Ordinal);

            var read = ProtocolJson.ReadRequest(json);
            Assert.IsType(req.GetType(), read);
            Assert.Equal(@"C:\wt1", read!.WorktreeRoot);
        }
    }

    [Fact]
    public void New_protocol_responses_and_repair_packet_round_trip_with_aot_json()
    {
        var repair = new ReportedRepairPacket(
            SourceWindow: ">   10: calc.Ad(1, 2);",
            Candidates: ["int Add(int a, int b)"],
            SuggestedUsings: ["using Lib;"],
            InterfaceStub: "public int Compute() => throw new NotImplementedException();");
        var err = new ReportedError(new CompilerError("App/Program.cs", 10, 5, "CS1061", "no Ad")) { Repair = repair };

        EngineResponse[] responses =
        [
            new EngineResponse.MemoryYielded(12_582_912, 240_000_000),
            new EngineResponse.JudgeAnswered(new JudgeReport(
                "race",
                true,
                "cand1 wins",
                "cand1",
                [new JudgeReport.CandidateVerdict("cand1", 1, true, 0, TestsPassed: true, TestsRun: 4, TestsFailed: 0, ElapsedMs: 85, Errors: [err])])),
            new EngineResponse.GraphAnswered(new GraphReport(
                "0123456789ab",
                [new GraphReport.ProjectNode("Lib", "Lib/Lib.csproj", [], ["App"], false, "net10.0", ["Lib/Calc.cs"])],
                [new GraphReport.CouplingCluster("core", ["Lib", "App"])],
                [new GraphReport.TestOwnership("Lib.Tests", ["Lib"])])),
            new EngineResponse.ImpactAnswered(new ImpactReport(["Lib/Calc.cs"], ["Lib"], ["Lib", "App"], ["App/Program.cs"], ["Lib.Tests"], false, "2 projects affected")),
            new EngineResponse.WatchEventPushed(new WatchEvent("guard_violation", Session: "w1", Generation: 3, Tree: "0123456789ab", GuardName: "contracts", ViolatedPattern: "src/Contracts/**", Declaration: "public interface IOrder")),
        ];

        foreach (var response in responses)
        {
            var json = ProtocolJson.Serialize(response);
            var read = ProtocolJson.ReadResponse(json);
            Assert.IsType(response.GetType(), read);
        }
    }

    [Fact]
    public void Check_brief_renders_repair_packets_and_fallback_source_window()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["App/Program.cs"] = "using System;\nclass Program\n{\n    void Run() { var x = unknownVar; }\n}\n",
        });

        var withPacket = new ReportedError(new CompilerError("App/Program.cs", 4, 26, "CS0103", "unknownVar"))
        {
            Repair = new ReportedRepairPacket(
                SourceWindow: ">    4:     void Run() { var x = unknownVar; }",
                Candidates: ["Run()"],
                SuggestedUsings: ["using App.Models;"],
                InterfaceStub: "void Missing();"),
        };
        var withoutPacket = new ReportedError(new CompilerError("App/Program.cs", 4, 26, "CS1002", "; expected"));

        var textResult = CheckOperation.Render(new CheckReport([withPacket, withoutPacket], 1, ["App"], [], 0, false, 2, 1, 0), brief: true, repoRootPath: repo.Path);
        Assert.Equal(Outcome.ProblemsFound, textResult.Outcome);
        Assert.Contains("using: using App.Models;", textResult.Text, StringComparison.Ordinal);
        Assert.Contains("candidates:\n    Run()", textResult.Text, StringComparison.Ordinal);
        Assert.Contains("stub:\n    void Missing();", textResult.Text, StringComparison.Ordinal);
        Assert.Contains(">    4:     void Run() { var x = unknownVar; }", textResult.Text, StringComparison.Ordinal);

        var jsonResult = CheckOperation.Render(new CheckReport([withPacket], 1, ["App"], [], 0, false, 1, 1, 0), json: true, brief: true, repoRootPath: repo.Path);
        using var parsed = JsonDocument.Parse(jsonResult.Text.Split('\n')[0]);
        Assert.Equal("using App.Models;", parsed.RootElement.GetProperty("repair").GetProperty("suggestedUsings")[0].GetString());
    }

    [Fact]
    public void Check_arguments_parses_brief_flag()
    {
        var (parsed, refusal) = CheckArguments.Parse(["--brief", "--json", "Lib/Calc.cs"]);

        Assert.Null(refusal);
        Assert.True(parsed!.Brief);
        Assert.True(parsed.Json);
        Assert.Equal(["Lib/Calc.cs"], parsed.Files);
    }

    [Theory]
    [InlineData("  Failed Lib.Tests.CalcTests.Adds [12 ms]", "Lib.Tests.CalcTests.Adds")]
    [InlineData("  failed Lib.Tests.CalcTests.Adds (12 ms)", "Lib.Tests.CalcTests.Adds")]
    [InlineData("[FAIL] Lib.Tests.CalcTests.Adds", "Lib.Tests.CalcTests.Adds")]
    [InlineData("  Passed Lib.Tests.CalcTests.Multiplies [1 ms]", null)]
    public void Test_operation_parses_failure_lines_for_early_stop(string line, string? expected) =>
        Assert.Equal(expected, TestOperation.TryParseFailureName(line));

    [Fact]
    public void Gate_argument_parser_handles_flags_and_refuses_invalid_values()
    {
        var (parsed, refusal) = GateOperation.Parse(["--session", "worker-1", "--brief", "--test", "--max-failures=5", "--json"]);
        Assert.Null(refusal);
        Assert.Equal(("worker-1", true, true, 5, true), (parsed!.Session, parsed.Brief, parsed.RunTests, parsed.MaxFailures, parsed.Json));

        var (bad, badRefusal) = GateOperation.Parse(["--max-failures", "0"]);
        Assert.Null(bad);
        Assert.NotNull(badRefusal);
    }

    [Fact]
    public async Task Judge_loads_patch_files_and_worktrees_and_renders_scorecard()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        repo.Write("cand1.patch", "--- a/Lib/Calc.cs\n+++ b/Lib/Calc.cs\n@@ -1 +1 @@\n-public class Calc {}\n+public class Calc { public int A => 1; }\n");
        repo.Replace("Lib/Calc.cs", "{}", "{ public int B => 2; }");
        repo.Write("Lib/NewFile.cs", "public class NewFile {}\n");

        var (parsed, refusal) = JudgeOperation.Parse(["--race", "cand1.patch", ".", "--test", "--json"]);
        Assert.Null(refusal);
        Assert.Equal(JudgeRequest.JudgeMode.Race, parsed!.Mode);
        Assert.True(parsed.RunTests);

        var (c1, e1) = await JudgeOperation.LoadCandidateAsync("cand1.patch", repo.Path, TestContext.Current.CancellationToken);
        var (c2, e2) = await JudgeOperation.LoadCandidateAsync(".", repo.Path, TestContext.Current.CancellationToken);
        Assert.Null(e1);
        Assert.Null(e2);
        Assert.Contains("public int A => 1", c1!.Diff, StringComparison.Ordinal);
        Assert.Contains("public int B => 2", c2!.Diff, StringComparison.Ordinal);
        Assert.Contains("+++ b/Lib/NewFile.cs", c2.Diff, StringComparison.Ordinal);

        var report = new JudgeReport(
            "race",
            true,
            "cand1 won",
            "cand1",
            [
                new JudgeReport.CandidateVerdict("cand1", 1, true, 0, TestsPassed: true, TestsRun: 2, TestsFailed: 0, ElapsedMs: 42),
                new JudgeReport.CandidateVerdict(".", 2, false, 1, ElapsedMs: 30, Errors: [new ReportedError(new CompilerError("Lib/Calc.cs", 1, 1, "CS1002", "; expected"))]),
            ]);
        var rendered = JudgeOperation.Render(report, json: false);
        Assert.Equal(Outcome.Clean, rendered.Outcome);
        Assert.Contains("#1 cand1: compiles, 2 test(s) passed", rendered.Text, StringComparison.Ordinal);
        Assert.Contains("winner: cand1", rendered.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Graph_and_impact_render_text_and_json()
    {
        var graph = new GraphReport(
            "0123456789ab",
            [new GraphReport.ProjectNode("Lib", "Lib/Lib.csproj", [], ["App"], false, "net10.0", ["Lib/Calc.cs"])],
            [new GraphReport.CouplingCluster("c1", ["Lib", "App"])],
            [new GraphReport.TestOwnership("Lib.Tests", ["Lib"])]);
        var graphText = GraphOperation.RenderGraph(graph, json: false);
        Assert.Contains("[src] Lib (Lib/Lib.csproj)", graphText.Text, StringComparison.Ordinal);
        Assert.Contains("tests Lib.Tests: Lib", graphText.Text, StringComparison.Ordinal);

        var impact = new ImpactReport(["Lib/Calc.cs"], ["Lib"], ["Lib", "App"], ["App/Program.cs"], ["Lib.Tests"], false, "2 affected");
        var impactText = GraphOperation.RenderImpact(impact, json: false);
        Assert.Contains("affected tests: Lib.Tests", impactText.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Slim_without_running_engine_returns_clean_without_starting_one()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });

        var text = await SlimOperation.RunAsync(repo.Root, json: false, TestContext.Current.CancellationToken);
        var json = await SlimOperation.RunAsync(repo.Root, json: true, TestContext.Current.CancellationToken);

        Assert.Equal(Outcome.Clean, text.Outcome);
        Assert.Contains("nothing to slim", text.Text, StringComparison.Ordinal);
        Assert.Equal(Outcome.Clean, json.Outcome);
        using var parsed = JsonDocument.Parse(json.Text);
        Assert.Equal("stopped", parsed.RootElement.GetProperty("engine").GetString());
    }

    [Fact]
    public async Task Session_guard_validates_name_and_patterns_before_sending()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });

        var badName = await SessionOperation.RunAsync(repo.Root, ["guard", "bad name!", "--freeze", "src/**"], TestContext.Current.CancellationToken);
        var badPattern = await SessionOperation.RunAsync(repo.Root, ["guard", "contracts", "--freeze", "../outside/**"], TestContext.Current.CancellationToken);
        var badUnfreeze = await SessionOperation.RunAsync(repo.Root, ["guard", "contracts", "--unfreeze", "extra"], TestContext.Current.CancellationToken);

        Assert.Equal(Outcome.Unanswered, badName.Outcome);
        Assert.Equal(Outcome.Unanswered, badPattern.Outcome);
        Assert.Equal(Outcome.Unanswered, badUnfreeze.Outcome);
    }

    [Fact]
    public async Task Watch_all_json_falls_back_to_polling_session_files_when_no_engine_runs()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
        var sessionsDir = Path.Combine(repo.Root.StateDirectory, "sessions");
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllText(
            Path.Combine(sessionsDir, "w1.json"),
            """{"session":"w1","generation":4,"tree":"0123456789ab","errorCount":1,"errors":[{"path":"Lib/Calc.cs","line":2,"column":3,"id":"CS0103","message":"missing","fromAnalyzer":false}]}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var output = new StringWriter();
        var watchTask = WatchOperation.RunAsync(repo.Root, session: null, all: true, json: true, output, cts.Token);

        while (!output.ToString().Contains("CS0103", StringComparison.Ordinal) && !cts.IsCancellationRequested)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var result = await watchTask;
        Assert.Equal(Outcome.Clean, result.Outcome);
        using var ev = JsonDocument.Parse(output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Equal("w1", ev.RootElement.GetProperty("session").GetString());
        Assert.Equal(1, ev.RootElement.GetProperty("errorCount").GetInt32());
    }

    [Theory]
    [InlineData("muse", """{"tool_name":"read_file","tool_input":{"path":"Lib/Calc.cs"}}""", true)]
    [InlineData("muse", """{"tool_name":"grep","tool_input":{"query":"Calc"}}""", true)]
    [InlineData("muse", """{"tool_name":"edit_file","tool_input":{"path":"Lib/Calc.cs"}}""", false)]
    [InlineData("claude", """{"tool_name":"Read","tool_input":{"file_path":"Lib/Calc.cs"}}""", true)]
    [InlineData("claude", """{"tool_name":"Grep","tool_input":{"pattern":"Calc"}}""", true)]
    [InlineData("claude", """{"tool_name":"Glob","tool_input":{"pattern":"*.cs"}}""", true)]
    [InlineData("claude", """{"tool_name":"LS","tool_input":{"path":"."}}""", true)]
    [InlineData("claude", """{"tool_name":"Edit","tool_input":{"file_path":"Lib/Calc.cs"}}""", false)]
    [InlineData("claude", "{not-json", false)]
    public void Pre_root_fast_peek_identifies_read_only_post_edit_payloads(string harness, string json, bool expectedFastExit) =>
        Assert.Equal(expectedFastExit, Program.IsReadOnlyPostEditPayload(harness, Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void OpenCode_plugin_carries_session_id_on_pre_shell_and_stop_and_direct_pipe_ipc()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("Fuse.Harnesses.opencode-plugin.js");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var js = reader.ReadToEnd();

        Assert.Contains("net.createConnection", js, StringComparison.Ordinal);
        Assert.Contains("rewrite(cwd, event.sessionID, event.input)", js, StringComparison.Ordinal);
        Assert.Contains("rewrite(directory, input.sessionID, output.args)", js, StringComparison.Ordinal);
        Assert.Contains("runHook(\"stop\", { cwd, session_id: sessionID, stop_hook_active: active }", js, StringComparison.Ordinal);
    }
}
