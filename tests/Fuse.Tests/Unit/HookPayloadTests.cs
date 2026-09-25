using Fuse.Check;
using Fuse.Cli;
using Fuse.Dotnet;
using Fuse.Engine;
using Fuse.Hooks;
using Fuse.Protocol;
using Fuse.Testing;

namespace Fuse.Tests.Unit;

public class HookPayloadTests
{
    private static readonly string Repo = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";

    private static string At(params string[] parts) => Path.GetFullPath(Path.Combine([Repo, .. parts]));

    private static HookPayload Parse(object payload) => HookPayload.Parse(System.Text.Json.JsonSerializer.Serialize(payload));

    [Fact]
    public void Claude_edit_payload()
    {
        var payload = Parse(new { hook_event_name = "PostToolUse", tool_name = "Edit", cwd = Repo, tool_input = new { file_path = At("src", "A.cs"), old_string = "a", new_string = "b" } });
        Assert.Equal([At("src", "A.cs")], payload.EditedFiles());
        Assert.False(payload.FromCursor);
    }

    [Fact]
    public void Cursor_payload_is_recognized()
    {
        var payload = Parse(new { hook_event_name = "postToolUse", cursor_version = "2.0", workspace_roots = new[] { Repo }, tool_input = new { file_path = "src/A.cs" } });
        Assert.True(payload.FromCursor);
        Assert.Equal([At("src", "A.cs")], payload.EditedFiles());
    }

    [Fact]
    public void Gemini_payload()
    {
        var payload = Parse(new { hook_event_name = "AfterTool", cwd = Repo, tool_name = "replace", tool_input = new { file_path = At("B.cs") } });
        Assert.Equal([At("B.cs")], payload.EditedFiles());
    }

    [Fact]
    public void Codex_apply_patch_payload()
    {
        var patch = "*** Begin Patch\n*** Update File: src/A.cs\n@@\n-a\n+b\n*** Add File: src/New.cs\n+x\n*** Delete File: old/Gone.cs\n*** End Patch";
        var payload = Parse(new { hook_event_name = "PostToolUse", cwd = Repo, tool_name = "apply_patch", tool_input = new { command = patch } });
        Assert.Equal([At("src", "A.cs"), At("src", "New.cs"), At("old", "Gone.cs")], payload.EditedFiles());
    }

    [Fact]
    public void Copilot_payload_with_string_arguments()
    {
        var arguments = System.Text.Json.JsonSerializer.Serialize(new { path = At("C.cs") });
        var payload = Parse(new { sessionId = "s", cwd = Repo, toolName = "edit", toolArgs = arguments });
        Assert.Equal([At("C.cs")], payload.EditedFiles());
    }

    [Fact]
    public void Stop_payload_flags()
    {
        Assert.True(HookPayload.Parse("""{"stop_hook_active":true}""").StopHookActive);
        Assert.True(HookPayload.Parse("""{"loop_count":1}""").StopHookActive);
        Assert.False(HookPayload.Parse("""{"stop_hook_active":false,"loop_count":0}""").StopHookActive);
    }

    [Fact]
    public void Bash_command_is_read()
    {
        Assert.Equal("dotnet test", HookPayload.Parse("""{"tool_input":{"command":"dotnet test","description":"d"}}""").Command);
    }

    [Fact]
    public void Empty_input_is_tolerated()
    {
        Assert.Empty(HookPayload.Parse("").EditedFiles());
    }
}
