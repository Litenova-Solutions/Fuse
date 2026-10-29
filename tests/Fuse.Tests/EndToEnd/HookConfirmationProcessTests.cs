using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     A background post-edit hook's answer against the current tree: an error fixed after the hook's check ran is not
///     reported again, the stop hook is not blocked by it, and hook.log tells a confirmed delivery from a drop and a stop
///     that blocks.
/// </summary>
public class HookConfirmationProcessTests
{
    [Fact]
    public async Task A_fixed_error_the_background_hook_reported_does_not_block_the_stop_hook()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
            var broken = await FuseProcess.RunAsync(repo.Path, PostEditPayload(repo, "Lib/Calc.cs"), "hook", "claude", "post-edit");
            Assert.Equal(2, broken.ExitCode);
            Assert.Contains("CS0103", broken.Stderr, StringComparison.Ordinal);

            repo.Replace("Lib/Calc.cs", "a * undefinedValue;", "a * b;");
            var afterFix = await FuseProcess.RunAsync(repo.Path, PostEditPayload(repo, "Lib/Calc.cs"), "hook", "claude", "post-edit");
            Assert.Equal(0, afterFix.ExitCode);
            Assert.Equal("", afterFix.Stdout + afterFix.Stderr);

            var stop = await FuseProcess.RunAsync(repo.Path, StopPayload(repo), "hook", "claude", "stop");
            Assert.Equal(0, stop.ExitCode);
            Assert.DoesNotContain("\"decision\":\"block\"", stop.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("CS0103", stop.Stdout + stop.Stderr, StringComparison.Ordinal);

            var hookLog = await File.ReadAllTextAsync(Path.Combine(repo.Root.StateDirectory, "hook.log"), TestContext.Current.CancellationToken);
            Assert.Contains("claude post-edit delivered: fuse: 1 error(s) introduced", hookLog, StringComparison.Ordinal);
            Assert.DoesNotContain("post-edit dropped:", hookLog, StringComparison.Ordinal);
            Assert.DoesNotContain("stop blocked:", hookLog, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_stop_that_blocks_names_its_errors_in_the_hook_log()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "public int Add(", "public int Plus(");
            var stop = await FuseProcess.RunAsync(repo.Path, StopPayload(repo), "hook", "claude", "stop");
            Assert.Equal(0, stop.ExitCode);
            Assert.Contains("\"decision\":\"block\"", stop.Stdout, StringComparison.Ordinal);

            var hookLog = await File.ReadAllTextAsync(Path.Combine(repo.Root.StateDirectory, "hook.log"), TestContext.Current.CancellationToken);
            Assert.Contains("claude stop blocked: fuse: ", hookLog, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    private static string PostEditPayload(FixtureRepo repo, string file) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "PostToolUse",
            ["tool_name"] = "Edit",
            ["cwd"] = repo.Path,
            ["tool_input"] = new Dictionary<string, string> { ["file_path"] = repo.Full(file) },
        });

    private static string StopPayload(FixtureRepo repo) =>
        $$"""{"hook_event_name":"Stop","stop_hook_active":false,"cwd":{{System.Text.Json.JsonSerializer.Serialize(repo.Path)}}}""";
}
