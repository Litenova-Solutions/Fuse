using Fuse.Engine;
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
            Assert.Contains("fuse: no new errors", first.Stdout, StringComparison.Ordinal);

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
    public async Task Shutdown_request_stops_the_engine()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "check")).ExitCode);
            Assert.True(FuseProcess.EngineRunning(repo.Root));
            var response = await FuseProcess.SendRawAsync(repo.Root, new EngineRequest(EngineVersion.Build, RequestKind.Shutdown));
            Assert.Equal(ResponseStatus.Ok, response?.Status);
            Assert.True(await FuseProcess.WaitForExitAsync(repo.Root, TimeSpan.FromSeconds(15)));
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task Client_of_another_version_gets_restart_and_the_engine_exits()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            Assert.Equal(0, (await FuseProcess.RunAsync(repo.Path, null, "check")).ExitCode);
            var response = await FuseProcess.SendRawAsync(repo.Root, new EngineRequest("0.0.0/other", RequestKind.Ping));
            Assert.Equal(ResponseStatus.Restart, response?.Status);
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
    public async Task Claude_post_edit_hook_wakes_the_agent_only_on_new_errors()
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
    public async Task Pre_bash_hook_rewrites_dotnet_test()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var result = await FuseProcess.RunAsync(repo.Path, """{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"dotnet test --no-build","description":"tests"}}""", "hook", "claude", "pre-bash");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("""{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"fuse test --no-build","description":"tests"}}}""", result.Stdout);
    }

    [Fact]
    public async Task OpenCode_pre_bash_hook_answers_with_the_command()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var result = await FuseProcess.RunAsync(repo.Path, """{"tool_input":{"command":"dotnet build -c Release"}}""", "hook", "opencode", "pre-bash");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("""{"command":"fuse build -c Release"}""", result.Stdout);
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
