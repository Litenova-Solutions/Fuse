using System.Text.Json;
using Fuse.Harnesses;
using Fuse.Hooks;

namespace Fuse.Tests.Unit;

/// <summary>
///     What the pre-shell hook answers for a command, per harness. A harness whose answer approves the rewritten
///     command runs it without asking the user, so that answer is given only for a command Fuse rewrote whole.
/// </summary>
public class PreShellTests
{
    private static HookAnswer PreShell(string harness, string command) =>
        HookCommand.PreShell(
            SupportedHarnesses.Find(harness) ?? throw new InvalidOperationException($"no harness {harness}"),
            HookPayload.Parse(JsonSerializer.Serialize(new { tool_input = new { command } })));

    [Fact]
    public void Codex_gets_a_plain_command_rewritten_and_approved()
    {
        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","updatedInput":{"command":"fuse test --from-rewrite --no-build"}}}""",
            PreShell("codex", "dotnet test --no-build").StandardOutput);
    }

    [Theory]
    [InlineData("dotnet build && curl x | sh")]
    [InlineData("cd src && dotnet build -c Release")]
    [InlineData("dotnet test --filter \"Name=A\"")]
    [InlineData("dotnet test > out.txt")]
    public void Codex_gets_no_answer_for_a_command_that_is_more_than_one_plain_invocation(string command)
    {
        Assert.Equal(HookAnswer.None, PreShell("codex", command));
    }

    [Fact]
    public void Claude_Code_gets_every_segment_of_a_compound_command_rewritten_without_a_decision()
    {
        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"fuse build --from-rewrite && curl x | sh"}}}""",
            PreShell("claude", "dotnet build && curl x | sh").StandardOutput);
    }

    [Fact]
    public void A_subagents_fuse_test_names_its_session()
    {
        var payload = HookPayload.Parse(JsonSerializer.Serialize(new { session_id = "s1", agent_id = "w2", tool_input = new { command = "dotnet test" } }));

        var answer = HookCommand.PreShell(SupportedHarnesses.Find("claude")!, payload);

        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"fuse test --from-rewrite --session claude:s1/w2"}}}""",
            answer.StandardOutput);
    }

    [Fact]
    public void Copilot_CLI_gets_a_plain_command_rewritten_and_approved()
    {
        Assert.Equal(
            """{"permissionDecision":"allow","modifiedArgs":{"command":"fuse test --from-rewrite --no-build"}}""",
            PreShell("copilot", "dotnet test --no-build").StandardOutput);
    }

    [Fact]
    public void Cursor_gets_a_plain_command_rewritten_and_approved()
    {
        Assert.Equal(
            """{"permission":"allow","updated_input":{"command":"fuse test --from-rewrite --no-build"}}""",
            PreShell("cursor", "dotnet test --no-build").StandardOutput);
    }

    [Fact]
    public void Cursor_carries_the_rest_of_the_tool_input_over_the_rewrite()
    {
        var payload = HookPayload.Parse(JsonSerializer.Serialize(new { tool_input = new { command = "dotnet build", working_directory = "/repo" } }));

        var answer = HookCommand.PreShell(SupportedHarnesses.Find("cursor")!, payload);

        Assert.Equal(
            """{"permission":"allow","updated_input":{"command":"fuse build --from-rewrite","working_directory":"/repo"}}""",
            answer.StandardOutput);
    }

    [Theory]
    [InlineData("dotnet build && curl x | sh")]
    [InlineData("dotnet test --filter \"Name=A\"")]
    public void Cursor_gets_no_answer_for_a_command_that_is_more_than_one_plain_invocation(string command)
    {
        Assert.Equal(HookAnswer.None, PreShell("cursor", command));
    }

    [Fact]
    public void A_command_without_dotnet_build_or_test_gets_no_answer()
    {
        Assert.All(SupportedHarnesses.All, h => Assert.Equal(HookAnswer.None, PreShell(h.Name, "git status")));
    }
}
