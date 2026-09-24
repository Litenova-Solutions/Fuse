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
    [Fact]
    public void Claude_edit_payload()
    {
        var payload = HookPayload.Parse("""{"hook_event_name":"PostToolUse","tool_name":"Edit","cwd":"C:\\repo","tool_input":{"file_path":"C:\\repo\\src\\A.cs","old_string":"a","new_string":"b"}}""");
        Assert.Equal([Path.GetFullPath(@"C:\repo\src\A.cs")], payload.EditedFiles());
        Assert.False(payload.FromCursor);
    }

    [Fact]
    public void Cursor_payload_is_recognized()
    {
        var payload = HookPayload.Parse("""{"hook_event_name":"postToolUse","cursor_version":"2.0","workspace_roots":["C:\\repo"],"tool_input":{"file_path":"src/A.cs"}}""");
        Assert.True(payload.FromCursor);
        Assert.Equal([Path.GetFullPath(@"C:\repo\src\A.cs")], payload.EditedFiles());
    }

    [Fact]
    public void Gemini_payload()
    {
        var payload = HookPayload.Parse("""{"hook_event_name":"AfterTool","cwd":"C:\\repo","tool_name":"replace","tool_input":{"file_path":"C:\\repo\\B.cs"}}""");
        Assert.Equal([Path.GetFullPath(@"C:\repo\B.cs")], payload.EditedFiles());
    }

    [Fact]
    public void Codex_apply_patch_payload()
    {
        var patch = "*** Begin Patch\n*** Update File: src/A.cs\n@@\n-a\n+b\n*** Add File: src/New.cs\n+x\n*** Delete File: old/Gone.cs\n*** End Patch";
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["hook_event_name"] = "PostToolUse",
            ["cwd"] = @"C:\repo",
            ["tool_name"] = "apply_patch",
            ["tool_input"] = new Dictionary<string, string> { ["command"] = patch },
        });
        var payload = HookPayload.Parse(json);
        Assert.Equal(
            [Path.GetFullPath(@"C:\repo\src\A.cs"), Path.GetFullPath(@"C:\repo\src\New.cs"), Path.GetFullPath(@"C:\repo\old\Gone.cs")],
            payload.EditedFiles());
    }

    [Fact]
    public void Copilot_payload_with_string_arguments()
    {
        var payload = HookPayload.Parse("""{"sessionId":"s","cwd":"C:\\repo","toolName":"edit","toolArgs":"{\"path\":\"C:\\\\repo\\\\C.cs\"}"}""");
        Assert.Equal([Path.GetFullPath(@"C:\repo\C.cs")], payload.EditedFiles());
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
