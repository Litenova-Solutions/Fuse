using System.Text.Json;
using Fuse.Harnesses;
using Fuse.Paths;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     Each harness's answer to each event, byte for byte, as the harness reads it. A harness that misreads an answer
///     does nothing with it and says nothing, so a changed field name or exit code would only show in the agent missing
///     its errors.
/// </summary>
public class HarnessTests
{
    private const string Report = "fuse: 1 error(s) introduced in 1 file(s) (Lib)";
    private const string Reason = "fix it";

    private static Harness Named(string name) => SupportedHarnesses.Find(name) ?? throw new InvalidOperationException($"no harness {name}");

    [Fact]
    public void Every_harness_is_found_by_its_command_line_name_and_by_nothing_else()
    {
        Assert.Equal(["claude", "cursor", "gemini", "codex", "copilot", "opencode"], SupportedHarnesses.All.Select(h => h.Name));
        Assert.IsType<ClaudeCode>(SupportedHarnesses.Find("claude"));
        Assert.IsType<Cursor>(SupportedHarnesses.Find("cursor"));
        Assert.IsType<GeminiCli>(SupportedHarnesses.Find("gemini"));
        Assert.IsType<Codex>(SupportedHarnesses.Find("codex"));
        Assert.IsType<CopilotCli>(SupportedHarnesses.Find("copilot"));
        Assert.IsType<OpenCode>(SupportedHarnesses.Find("opencode"));
        Assert.Null(SupportedHarnesses.Find("Claude"));
        Assert.Null(SupportedHarnesses.Find("vscode"));
        Assert.Null(SupportedHarnesses.Find(""));
    }

    [Fact]
    public void Only_Claude_Code_runs_post_edit_in_the_background_and_has_its_hooks_run_by_Cursor_too()
    {
        Assert.Equal(["claude"], SupportedHarnesses.All.Where(h => h.RunsPostEditInBackground).Select(h => h.Name));
        Assert.Equal(["claude"], SupportedHarnesses.All.Where(h => h.IsAlsoRunByCursor).Select(h => h.Name));
    }

    [Theory]
    [InlineData("claude", """{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"fuse test --no-build","description":"tests"}}}""")]
    [InlineData("cursor", "")]
    [InlineData("gemini", """{"hookSpecificOutput":{"hookEventName":"BeforeTool","tool_input":{"command":"fuse test --no-build"}}}""")]
    [InlineData("codex", """{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"allow","updatedInput":{"command":"fuse test --no-build"}}}""")]
    [InlineData("copilot", "")]
    [InlineData("opencode", """{"command":"fuse test --no-build"}""")]
    public void A_rewritten_shell_command_is_answered_in_the_harness_format(string harness, string output)
    {
        using var input = JsonDocument.Parse("""{"command":"dotnet test --no-build","description":"tests"}""");
        Assert.Equal(new HookAnswer(output, "", 0), Named(harness).ReplaceShellCommand(input.RootElement, "fuse test --no-build"));
    }

    [Fact]
    public void Claude_Code_gets_a_whole_tool_input_even_when_the_harness_sent_none()
    {
        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PreToolUse","updatedInput":{"command":"fuse build"}}}""",
            new ClaudeCode().ReplaceShellCommand(null, "fuse build").StandardOutput);
    }

    [Theory]
    [InlineData("claude", "", Report, 2)]
    [InlineData("cursor", """{"additional_context":"fuse: 1 error(s) introduced in 1 file(s) (Lib)"}""", "", 0)]
    [InlineData("gemini", """{"hookSpecificOutput":{"hookEventName":"AfterTool","additionalContext":"fuse: 1 error(s) introduced in 1 file(s) (Lib)"}}""", "", 0)]
    [InlineData("codex", """{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"fuse: 1 error(s) introduced in 1 file(s) (Lib)"}}""", "", 0)]
    [InlineData("copilot", """{"additionalContext":"fuse: 1 error(s) introduced in 1 file(s) (Lib)"}""", "", 0)]
    [InlineData("opencode", """{"additionalContext":"fuse: 1 error(s) introduced in 1 file(s) (Lib)"}""", "", 0)]
    public void A_post_edit_report_is_answered_in_the_harness_format(string harness, string output, string error, int exitCode)
    {
        Assert.Equal(new HookAnswer(output, error, exitCode), Named(harness).ReportAfterEdit(Report));
    }

    [Theory]
    [InlineData("claude", "")]
    [InlineData("cursor", "{}")]
    [InlineData("gemini", "{}")]
    [InlineData("codex", "{}")]
    [InlineData("copilot", "{}")]
    [InlineData("opencode", "{}")]
    public void A_stop_with_nothing_to_fix_is_answered_in_the_harness_format(string harness, string output)
    {
        Assert.Equal(new HookAnswer(output, "", 0), Named(harness).AllowStop());
    }

    [Theory]
    [InlineData("claude", """{"decision":"block","reason":"fix it"}""")]
    [InlineData("cursor", """{"followup_message":"fix it"}""")]
    [InlineData("gemini", """{"decision":"deny","reason":"fix it"}""")]
    [InlineData("codex", """{"decision":"block","reason":"fix it"}""")]
    [InlineData("copilot", """{"decision":"block","reason":"fix it"}""")]
    [InlineData("opencode", """{"decision":"block","reason":"fix it"}""")]
    public void A_stop_that_sends_the_agent_back_is_answered_in_the_harness_format(string harness, string output)
    {
        Assert.Equal(new HookAnswer(output, "", 0), Named(harness).BlockStop(Reason));
    }

    [Fact]
    public void Quotes_and_angle_brackets_in_a_compiler_error_stay_readable()
    {
        var answer = new Codex().ReportAfterEdit("A.cs(3,5): error CS0246: 'List<int>' & \"T\" not found\nfuse: done");
        Assert.Equal(
            """{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"A.cs(3,5): error CS0246: 'List<int>' & \"T\" not found\nfuse: done"}}""",
            answer.StandardOutput);
    }

    [Theory]
    [InlineData("claude", ".claude/")]
    [InlineData("claude", "CLAUDE.md")]
    [InlineData("cursor", ".cursor/")]
    [InlineData("gemini", ".gemini/")]
    [InlineData("gemini", "GEMINI.md")]
    [InlineData("codex", ".codex/")]
    [InlineData("copilot", ".github/copilot-instructions.md")]
    [InlineData("copilot", ".github/hooks/")]
    [InlineData("opencode", ".opencode/")]
    [InlineData("opencode", "opencode.json")]
    [InlineData("opencode", "opencode.jsonc")]
    public void A_harness_is_detected_by_each_of_its_markers_and_no_other_harness_is(string harness, string marker)
    {
        var directory = FixtureRepo.NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            var path = Path.Combine(directory, marker.TrimEnd('/'));
            if (marker.EndsWith('/'))
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
            }

            var root = RepoRoot.Find(directory)!;
            Assert.Equal([harness], SupportedHarnesses.All.Where(h => h.IsUsedIn(root)).Select(h => h.Name));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_repository_without_markers_uses_no_harness()
    {
        var directory = FixtureRepo.NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            Directory.CreateDirectory(Path.Combine(directory, ".github"));
            var root = RepoRoot.Find(directory)!;
            Assert.DoesNotContain(SupportedHarnesses.All, h => h.IsUsedIn(root));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
