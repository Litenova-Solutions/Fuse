using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Harnesses;
using Fuse.Hooks;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     Muse Code: <c>fuse init</c> writes a plugin bundle Muse installs and approves, and <c>fuse hook muse</c> reads its
///     payloads and answers in its shape. The payloads are the ones Muse Code 1.4.10 sent its hooks.
/// </summary>
public class MuseCodeTests
{
    private static readonly MuseCode Muse = new();

    [Fact]
    public void Init_writes_a_plugin_bundle_with_the_three_hooks_and_says_how_to_install_it()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />", [".muse/.keep"] = "" });
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, InitCommand.Run(repo.Root.Path, output, error));

        var manifest = JsonNode.Parse(repo.Read(".muse/plugins/fuse/.muse-plugin/plugin.json"))!.AsObject();
        Assert.Equal("fuse", manifest["name"]!.GetValue<string>());
        Assert.Equal(".muse-plugin", manifest["compat"]!["manifestDir"]!.GetValue<string>());
        var hooks = manifest["capabilities"]!["hooks"]!.AsArray().Select(h => (Event: h!["event"]!.GetValue<string>(), Command: string.Join(' ', h["command"]!.AsArray().Select(a => a!.GetValue<string>())))).ToList();
        Assert.Equal(
            [("PostToolUse", "fuse hook muse post-edit"), ("PreToolUse", "fuse hook muse pre-shell"), ("Stop", "fuse hook muse stop")],
            hooks);
        Assert.Contains("muse plugins install .muse/plugins/fuse --scope project", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("muse plugins approve fuse", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("edit_file", true)]
    [InlineData("write_file", true)]
    [InlineData("apply_patch", true)]
    [InlineData("read_file", false)]
    [InlineData("search", false)]
    [InlineData(null, false)]
    public void Only_a_tool_that_writes_files_counts_as_an_edit(string? tool, bool writes)
    {
        Assert.Equal(writes, Muse.WritesFiles(tool));
    }

    [Fact]
    public void A_post_edit_event_after_a_read_is_not_checked()
    {
        var payload = HookPayload.Parse("""{"hook_event_name":"PostToolUse","tool_name":"read_file","tool_input":{"path":"A.cs"},"session_id":"s1","cwd":"C:\\repo"}""");

        Assert.Equal("read_file", payload.ToolName);
        Assert.False(Muse.WritesFiles(payload.ToolName));
    }

    [Fact]
    public void An_edit_names_its_file_relative_to_the_working_directory()
    {
        var cwd = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";
        var payload = HookPayload.Parse(JsonSerializer.Serialize(new { hook_event_name = "PostToolUse", tool_name = "edit_file", tool_input = new { find = "a", path = "Lib/A.cs", replace = "b" }, session_id = "s1", cwd }));

        Assert.Equal([Path.GetFullPath(Path.Combine(cwd, "Lib/A.cs"))], payload.EditedFiles());
        Assert.Equal("s1", payload.Session);
    }

    [Fact]
    public void A_patch_in_any_field_names_its_files()
    {
        var payload = HookPayload.Parse(JsonSerializer.Serialize(new { tool_name = "apply_patch", tool_input = new { input = "*** Begin Patch\n*** Update File: /repo/Lib/A.cs\n@@\n-a\n+b\n*** End Patch\n" }, cwd = "/repo" }));

        Assert.Contains(payload.EditedFiles(), p => p.EndsWith("A.cs", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("true", true)]
    public void Stop_hook_active_reads_the_string_Muse_sends(string value, bool active)
    {
        Assert.Equal(active, HookPayload.Parse($$"""{"hook_event_name":"Stop","stop_hook_active":{{value}}}""").StopHookActive);
    }

    [Fact]
    public void A_rewritten_command_keeps_the_tools_other_fields_and_is_allowed()
    {
        using var input = JsonDocument.Parse("""{"command":"dotnet test","description":"Run tests","workdir":"C:\\repo"}""");

        var answer = JsonNode.Parse(Muse.ReplaceShellCommand(input.RootElement, "fuse test --session muse:s1").StandardOutput)!;

        var output = answer["hookSpecificOutput"]!;
        Assert.Equal("PreToolUse", output["hookEventName"]!.GetValue<string>());
        Assert.Equal("allow", output["permissionDecision"]!.GetValue<string>());
        Assert.Equal("fuse test --session muse:s1", output["updatedInput"]!["command"]!.GetValue<string>());
        Assert.Equal(@"C:\repo", output["updatedInput"]!["workdir"]!.GetValue<string>());
    }

    [Fact]
    public void A_report_after_an_edit_is_a_block_reason_Muse_shows_the_model()
    {
        var answer = JsonNode.Parse(Muse.ReportAfterEdit("App/A.cs(1,1): error CS0103: x").StandardOutput)!;

        Assert.Equal("block", answer["decision"]!.GetValue<string>());
        Assert.Equal("App/A.cs(1,1): error CS0103: x", answer["reason"]!.GetValue<string>());
    }
}
