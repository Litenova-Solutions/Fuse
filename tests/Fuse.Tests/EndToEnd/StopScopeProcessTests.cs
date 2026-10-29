using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     Each session's stop is blocked on its own errors only, and the left-out count names only the other session's
///     errors in reach, not those only its own files hold.
/// </summary>
public class StopScopeProcessTests
{
    [Fact]
    public async Task Two_sessions_stops_each_name_one_error_left_out()
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

            repo.Replace("Lib/Greeting.cs", "string Greet(string name);", "string Hail(string name);");
            await FuseProcess.RunAsync(repo.Path, Payload("b", "Lib/Greeting.cs"), "hook", "claude", "post-edit");
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            await FuseProcess.RunAsync(repo.Path, Payload("a", "Lib/Calc.cs"), "hook", "claude", "post-edit");

            string Stop(string session) => $$"""{"session_id":"{{session}}","hook_event_name":"Stop","stop_hook_active":false,"cwd":{{System.Text.Json.JsonSerializer.Serialize(repo.Path)}}}""";
            var stop = await FuseProcess.RunAsync(repo.Path, Stop("a"), "hook", "claude", "stop");
            Assert.Contains("\"decision\":\"block\"", stop.Stdout, StringComparison.Ordinal);
            Assert.Contains("'Add'", stop.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("'Greet'", stop.Stdout, StringComparison.Ordinal);
            Assert.Contains("1 error(s) from other sessions' edits left out", stop.Stdout, StringComparison.Ordinal);
            Assert.Contains("no other session's edits cause them on their own", stop.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }
}
