using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

public class EngineProcessTests
{
    [Fact]
    public async Task Check_starts_an_engine_and_the_next_call_reuses_it()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            var first = await FuseProcess.RunAsync(repo.Path, null, "check");
            Assert.Equal(0, first.ExitCode);
            Assert.Contains("fuse: no errors introduced", first.Stdout, StringComparison.Ordinal);

            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            var second = await FuseProcess.RunAsync(repo.Path, null, "check", "Lib/Calc.cs");
            Assert.Equal(1, second.ExitCode);
            Assert.Contains("App/Program.cs", second.Stdout, StringComparison.Ordinal);
            Assert.Contains("error CS1061", second.Stdout, StringComparison.Ordinal);

            var log = File.ReadAllLines(Path.Combine(repo.Root.StateDirectory, "engine.log"));
            Assert.Single(log, l => l.Contains(" started for ", StringComparison.Ordinal));
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_generated_regex_with_a_culture_name_is_not_an_error()
    {
        // The engine runs the repository's source generators, and the Regex generator looks the culture up by name. With
        // invariant globalization and only predefined cultures, that lookup fails and the method gets no implementation.
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Write("Lib/Patterns.cs", """
                using System.Text.RegularExpressions;

                namespace Lib;

                public static partial class Patterns
                {
                    [GeneratedRegex("^[a-z]+$", RegexOptions.IgnoreCase, "en-US")]
                    public static partial Regex Word();
                }

                """);
            var result = await FuseProcess.RunAsync(repo.Path, null, "check");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("fuse: no errors introduced", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Shutdown_request_stops_the_engine()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "check")).ExitCode);
            Assert.True(FuseProcess.EngineRunning(repo.Root));
            var response = await FuseProcess.SendRawAsync(repo.Root, new EngineRequest.ShutDown { BuildId = EngineVersion.Build });
            Assert.IsType<EngineResponse.Acknowledged>(response);
            Assert.True(await FuseProcess.WaitForExitAsync(repo.Root, TimeSpan.FromSeconds(15)));
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Client_of_another_build_gets_restart_and_the_engine_exits()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "check")).ExitCode);
            var response = await FuseProcess.SendRawAsync(repo.Root, new EngineRequest.Ping { BuildId = "0.0.0/other" });
            Assert.IsType<EngineResponse.Restart>(response);
            Assert.True(await FuseProcess.WaitForExitAsync(repo.Root, TimeSpan.FromSeconds(15)));

            // The next client starts a matching engine.
            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "check")).ExitCode);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_request_in_the_shape_of_an_earlier_build_gets_restart()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "check")).ExitCode);
            // How a 5.0.0 client writes a check: no request case for this build to read, and the build id under another name.
            var response = await FuseProcess.SendLineAsync(repo.Root, """{"version":"5.0.0/0","kind":"Check","wait":true}""");
            Assert.IsType<EngineResponse.Restart>(response);
            Assert.True(await FuseProcess.WaitForExitAsync(repo.Root, TimeSpan.FromSeconds(15)));
            // The line carries no build id under this build's name, and the log says so rather than leaving a gap.
            var log = File.ReadAllLines(Path.Combine(repo.Root.StateDirectory, "engine.log"));
            Assert.Contains(log, l => l.EndsWith(" client build (none) differs; exiting so the client can start a matching engine", StringComparison.Ordinal));
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Claude_post_edit_hook_wakes_the_agent_only_on_introduced_errors()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            string Payload(string file) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["hook_event_name"] = "PostToolUse",
                ["tool_name"] = "Edit",
                ["cwd"] = repo.Path,
                ["tool_input"] = new Dictionary<string, string> { ["file_path"] = repo.Full(file) },
            });

            repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            var clean = await FuseProcess.RunAsync(repo.Path, Payload("Lib/Calc.cs"), "hook", "claude", "post-edit");
            Assert.Equal(0, clean.ExitCode);
            Assert.Equal("", clean.Stdout + clean.Stderr);

            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            var broken = await FuseProcess.RunAsync(repo.Path, Payload("Lib/Calc.cs"), "hook", "claude", "post-edit");
            Assert.Equal(2, broken.ExitCode);
            Assert.Contains("Lib.Tests/CalcTests.cs", broken.Stderr, StringComparison.Ordinal);

            var stop = await FuseProcess.RunAsync(repo.Path, $$"""{"hook_event_name":"Stop","stop_hook_active":false,"cwd":{{System.Text.Json.JsonSerializer.Serialize(repo.Path)}}}""", "hook", "claude", "stop");
            Assert.Equal(0, stop.ExitCode);
            Assert.Contains("\"decision\":\"block\"", stop.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Two_claude_sessions_are_each_told_their_own_errors_and_the_stop_hook_reports_all()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            string Payload(string session, string file) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["session_id"] = session,
                ["hook_event_name"] = "PostToolUse",
                ["tool_name"] = "Edit",
                ["cwd"] = repo.Path,
                ["tool_input"] = new Dictionary<string, string> { ["file_path"] = repo.Full(file) },
            });

            // Session b renames the interface's Greet, then session a renames Calc.Add. App/Program.cs calls both.
            repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
            var b = await FuseProcess.RunAsync(repo.Path, Payload("b", "Lib/Greeting.cs"), "hook", "claude", "post-edit");
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            var a = await FuseProcess.RunAsync(repo.Path, Payload("a", "Lib/Calc.cs"), "hook", "claude", "post-edit");

            Assert.Equal(2, b.ExitCode);
            Assert.Equal(2, a.ExitCode);
            Assert.Contains("'Add'", a.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("'Greet'", a.Stderr, StringComparison.Ordinal);
            Assert.Contains("1 error(s) from other sessions' edits left out", a.Stderr, StringComparison.Ordinal);

            var stop = await FuseProcess.RunAsync(repo.Path, $$"""{"session_id":"a","hook_event_name":"Stop","stop_hook_active":false,"cwd":{{System.Text.Json.JsonSerializer.Serialize(repo.Path)}}}""", "hook", "claude", "stop");
            Assert.Contains("\"decision\":\"block\"", stop.Stdout, StringComparison.Ordinal);
            Assert.Contains("'Greet'", stop.Stdout, StringComparison.Ordinal);
            Assert.Contains("'Add'", stop.Stdout, StringComparison.Ordinal);
            Assert.Contains("The working tree has them and HEAD does not", stop.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("your changes introduced them", stop.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Post_edit_hook_tells_the_agent_to_restore_an_unrestored_project()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            File.Delete(repo.Full("Lib/obj/project.assets.json"));
            repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["hook_event_name"] = "PostToolUse",
                ["tool_name"] = "Edit",
                ["cwd"] = repo.Path,
                ["tool_input"] = new Dictionary<string, string> { ["file_path"] = repo.Full("Lib/Calc.cs") },
            });

            // A missing restore is one of the two things a hook reports, so the agent is woken with the fix, not told nothing.
            var result = await FuseProcess.RunAsync(repo.Path, payload, "hook", "claude", "post-edit");
            Assert.Equal(2, result.ExitCode);
            Assert.Contains("dotnet restore", result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Stop_hook_sends_the_agent_back_to_restore_without_calling_it_an_introduced_error()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            File.Delete(repo.Full("Lib/obj/project.assets.json"));
            repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            var payload = $$"""{"hook_event_name":"Stop","stop_hook_active":false,"cwd":{{System.Text.Json.JsonSerializer.Serialize(repo.Path)}}}""";

            var result = await FuseProcess.RunAsync(repo.Path, payload, "hook", "claude", "stop");
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("\"decision\":\"block\"", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("dotnet restore", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("introduced", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_hook_with_an_empty_cwd_exits_zero_without_output()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var result = await FuseProcess.RunAsync(repo.Path, """{"hook_event_name":"PostToolUse","tool_name":"Edit","cwd":"","tool_input":{"file_path":""}}""", "hook", "claude", "post-edit");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout + result.Stderr);
    }

    [Fact]
    public async Task Pre_shell_hook_rewrites_dotnet_test()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var result = await FuseProcess.RunAsync(repo.Path, """{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"dotnet test --no-build","description":"tests"}}""", "hook", "claude", "pre-shell");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"fuse test --no-build","description":"tests"}}}""", result.Stdout);
    }

    [Fact]
    public async Task OpenCode_pre_shell_hook_answers_with_the_command()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var result = await FuseProcess.RunAsync(repo.Path, """{"tool_input":{"command":"dotnet build -c Release"}}""", "hook", "opencode", "pre-shell");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("""{"command":"fuse build -c Release"}""", result.Stdout);
    }

    [Fact]
    public async Task Pre_bash_is_not_an_event_and_only_prints_usage()
    {
        // A registration from an earlier release calls pre-bash, which is not an event. It rewrites nothing and exits 0,
        // so the agent's command runs as it was, until the user reruns fuse init.
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var result = await FuseProcess.RunAsync(repo.Path, """{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"dotnet test --no-build"}}""", "hook", "claude", "pre-bash");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("usage: fuse hook <claude|cursor|gemini|codex|copilot|opencode> <post-edit|pre-shell|stop>", result.Stderr.TrimEnd());
    }

    [Fact]
    public async Task An_unknown_harness_only_prints_usage()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var result = await FuseProcess.RunAsync(repo.Path, """{"tool_input":{"command":"dotnet test"}}""", "hook", "vscode", "pre-shell");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("usage: fuse hook <claude|cursor|gemini|codex|copilot|opencode> <post-edit|pre-shell|stop>", result.Stderr.TrimEnd());
    }

    [Theory]
    [InlineData("--all", "--filter", "Name=A")]
    [InlineData("Lib.Tests", "--all")]
    public async Task Test_all_with_other_arguments_is_a_usage_failure(params string[] arguments)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var result = await FuseProcess.RunAsync(repo.Path, null, ["test", .. arguments]);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("", result.Stdout);
        Assert.Equal("fuse: --all runs every test and cannot be combined with other arguments; pass the arguments without --all to choose the scope", result.Stderr);
    }

    [Theory]
    [InlineData("claude", "post-edit", "[]")]
    [InlineData("claude", "stop", "null")]
    [InlineData("cursor", "post-edit", "\"x\"")]
    [InlineData("codex", "pre-shell", "42")]
    public async Task A_hook_payload_that_is_json_but_not_an_object_exits_zero_without_output(string harness, string hookEvent, string payload)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var result = await FuseProcess.RunAsync(repo.Path, payload, "hook", harness, hookEvent);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("", result.Stdout + result.Stderr);
        Assert.Contains("payload was not valid JSON", File.ReadAllText(Path.Combine(repo.Root.StateDirectory, "hook.log")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Check_of_an_empty_file_argument_is_refused_with_exit_code_2(string file)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Lib.csproj"] = "<Project />" });
        var result = await FuseProcess.RunAsync(repo.Path, null, "check", "Lib/Calc.cs", file);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal("fuse: a file named in the check is empty; name each file by its path", result.Stdout);
        Assert.Equal("", result.Stderr);
    }

    [Fact]
    public async Task Hook_outside_a_repository_is_silent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-no-repo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["tool_input"] = new Dictionary<string, string> { ["file_path"] = Path.Combine(directory, "A.cs") },
            });
            var result = await FuseProcess.RunAsync(directory, payload, "hook", "claude", "post-edit");
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("", result.Stdout + result.Stderr);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
