using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary><c>fuse check</c> names the changed files it does not check, and hooks stay silent about them on their own.</summary>
public class NonCSharpProcessTests
{
    [Fact]
    public async Task Fuse_check_names_non_csharp_changes()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            repo.Write("notes/app.tsx", "export const x = 1;\n");

            var check = await FuseProcess.RunAsync(repo.Path, null, "check");

            Assert.Equal(0, check.ExitCode);
            Assert.Contains("1 non-C# file(s) changed, which Fuse does not check: notes/app.tsx", check.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_stop_hook_clean_of_introduced_errors_stays_silent_about_them()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
            repo.Write("notes/app.tsx", "export const x = 1;\n");

            var stop = await FuseProcess.RunAsync(repo.Path, StopPayload(repo), "hook", "claude", "stop");

            Assert.Equal(0, stop.ExitCode);
            Assert.DoesNotContain("\"decision\":\"block\"", stop.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("non-C#", stop.Stdout + stop.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            await FuseProcess.StopEngineAsync(repo.Root);
        }
    }

    [Fact]
    public async Task A_post_edit_hook_with_errors_leaves_the_note_out()
    {
        using var repo = FixtureRepo.CreateStandard();
        try
        {
            repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
            repo.Write("notes/app.tsx", "export const x = 1;\n");

            var edited = await FuseProcess.RunAsync(repo.Path, PostEditPayload(repo, "Lib/Calc.cs"), "hook", "claude", "post-edit");

            Assert.Equal(2, edited.ExitCode);
            Assert.Contains("CS0103", edited.Stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("non-C#", edited.Stdout + edited.Stderr, StringComparison.Ordinal);
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
