using System.Text.Json.Nodes;
using Fuse.Harnesses;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class InitCommandTests
{
    private static int Init(FixtureRepo repo)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        return InitCommand.Run(repo.Root.Path, output, error);
    }

    private static JsonObject Json(FixtureRepo repo, string relative) => JsonNode.Parse(repo.Read(relative))!.AsObject();

    [Fact]
    public void Defaults_to_claude_code_when_no_harness_is_present()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />" });
        Assert.Equal(0, Init(repo));
        var hooks = Json(repo, ".claude/settings.json")["hooks"]!;
        var post = hooks["PostToolUse"]![0]!;
        Assert.Equal("Edit|Write|MultiEdit", post["matcher"]!.GetValue<string>());
        Assert.Equal("fuse hook claude post-edit", post["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.True(post["hooks"]![0]!["asyncRewake"]!.GetValue<bool>());
        Assert.Equal("Bash", hooks["PreToolUse"]![0]!["matcher"]!.GetValue<string>());
        Assert.Equal("fuse hook claude pre-shell", hooks["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("fuse hook claude stop", hooks["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.False(File.Exists(repo.Full(".cursor/hooks.json")));
    }

    [Fact]
    public void Preserves_existing_settings_and_is_idempotent()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["App/App.csproj"] = "<Project />",
            [".claude/settings.json"] = """
                {
                  // user comment
                  "model": "opus",
                  "hooks": {
                    "PostToolUse": [
                      { "matcher": "Write", "hooks": [ { "type": "command", "command": "prettier --write" } ] },
                      { "matcher": "Edit", "hooks": [ { "type": "command", "command": "fuse hook claude post-edit --old" } ] }
                    ]
                  },
                  "permissions": { "allow": [ "Bash(dotnet test:*)", "Read" ] }
                }
                """,
        });
        Assert.Equal(0, Init(repo));
        Assert.Equal(0, Init(repo));

        var settings = Json(repo, ".claude/settings.json");
        Assert.Equal("opus", settings["model"]!.GetValue<string>());
        var post = settings["hooks"]!["PostToolUse"]!.AsArray();
        var commands = post.SelectMany(g => g!["hooks"]!.AsArray()).Select(h => h!["command"]!.GetValue<string>()).ToList();
        Assert.Equal(["prettier --write", "fuse hook claude post-edit"], commands);
        var allow = settings["permissions"]!["allow"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        Assert.Equal(["Bash(dotnet test:*)", "Read", "Bash(fuse test:*)"], allow);
    }

    [Fact]
    public void Rerunning_init_replaces_a_pre_bash_registration_with_pre_shell()
    {
        // A registration an earlier build wrote calls an event fuse hook no longer accepts; rerunning init replaces it.
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["App/App.csproj"] = "<Project />",
            [".claude/settings.json"] = """
                { "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "fuse hook claude pre-bash", "timeout": 10 } ] } ] } }
                """,
            [".codex/hooks.json"] = """
                { "hooks": { "PreToolUse": [ { "matcher": "^Bash$", "hooks": [ { "type": "command", "command": "fuse hook codex pre-bash", "timeout": 10 } ] } ] } }
                """,
        });
        Assert.Equal(0, Init(repo));

        foreach (var (file, harness) in new[] { (".claude/settings.json", "claude"), (".codex/hooks.json", "codex") })
        {
            var groups = Json(repo, file)["hooks"]!["PreToolUse"]!.AsArray();
            var commands = groups.SelectMany(g => g!["hooks"]!.AsArray()).Select(h => h!["command"]!.GetValue<string>());
            Assert.Equal([$"fuse hook {harness} pre-shell"], commands);
        }
    }

    [Fact]
    public void Does_not_add_permissions_the_user_had_not_granted()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["App/App.csproj"] = "<Project />",
            [".claude/settings.json"] = """{ "permissions": { "allow": [ "Read" ] } }""",
        });
        Init(repo);
        var allow = Json(repo, ".claude/settings.json")["permissions"]!["allow"]!.AsArray().Select(r => r!.GetValue<string>());
        Assert.Equal(["Read"], allow);
    }

    [Fact]
    public void Writes_every_detected_harness()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["App/App.csproj"] = "<Project />",
            [".cursor/rules.md"] = "x",
            [".gemini/settings.json"] = "{}",
            [".codex/config.toml"] = "",
            [".github/copilot-instructions.md"] = "x",
            ["opencode.json"] = "{}",
            [".vscode/settings.json"] = "{}",
        });
        Assert.Equal(0, Init(repo));
        Assert.False(File.Exists(repo.Full(".claude/settings.json")));

        var cursor = Json(repo, ".cursor/hooks.json");
        Assert.Equal(1, cursor["version"]!.GetValue<int>());
        Assert.Equal("fuse hook cursor post-edit", cursor["hooks"]!["postToolUse"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("fuse hook cursor stop", cursor["hooks"]!["stop"]![0]!["command"]!.GetValue<string>());

        var gemini = Json(repo, ".gemini/settings.json")["hooks"]!;
        Assert.Equal("write_file|replace", gemini["AfterTool"]![0]!["matcher"]!.GetValue<string>());
        Assert.Equal(60000, gemini["AfterTool"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>());
        Assert.Equal("run_shell_command", gemini["BeforeTool"]![0]!["matcher"]!.GetValue<string>());
        Assert.Equal("fuse hook gemini pre-shell", gemini["BeforeTool"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());

        var codex = Json(repo, ".codex/hooks.json")["hooks"]!;
        Assert.Equal("^apply_patch$", codex["PostToolUse"]![0]!["matcher"]!.GetValue<string>());
        Assert.Equal("^Bash$", codex["PreToolUse"]![0]!["matcher"]!.GetValue<string>());
        Assert.Equal("fuse hook codex pre-shell", codex["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());

        var copilot = Json(repo, ".github/hooks/fuse.json")["hooks"]!;
        Assert.Equal("fuse hook copilot post-edit", copilot["postToolUse"]![0]!["bash"]!.GetValue<string>());

        var opencode = repo.Read(".opencode/plugins/fuse.js");
        Assert.Contains("\"hook\", \"opencode\", event", opencode, StringComparison.Ordinal);
        Assert.Contains("\"tool.execute.after\"", opencode, StringComparison.Ordinal);
        Assert.Contains("fuse(\"pre-shell\"", opencode, StringComparison.Ordinal);
        Assert.DoesNotContain("pre-bash", opencode, StringComparison.Ordinal);

        var vscode = Json(repo, ".vscode/mcp.json")["servers"]!["fuse"]!;
        Assert.Equal("fuse", vscode["command"]!.GetValue<string>());
        Assert.Equal("mcp", vscode["args"]![0]!.GetValue<string>());
    }

    [Fact]
    public void A_repository_that_uses_only_vs_code_gets_the_mcp_server_and_no_hooks()
    {
        // VS Code is not a harness, but it counts as a sign of one: Claude Code is the default only without either.
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["App/App.csproj"] = "<Project />",
            [".vscode/settings.json"] = "{}",
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, InitCommand.Run(repo.Root.Path, output, error));
        Assert.Equal("fuse", Json(repo, ".vscode/mcp.json")["servers"]!["fuse"]!["command"]!.GetValue<string>());
        Assert.False(Directory.Exists(repo.Full(".claude")));
        Assert.StartsWith("wrote .vscode/mcp.json" + Environment.NewLine + "fuse: hooks installed", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    [InlineData("// only a comment\n/* and another */\n")]
    public void An_empty_or_comment_only_settings_file_reads_as_an_empty_object(string content)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />", [".claude/settings.json"] = content });
        Assert.Equal(0, Init(repo));
        Assert.Equal("fuse hook claude stop", Json(repo, ".claude/settings.json")["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void A_settings_file_that_is_not_json_is_named_and_left_as_it_was()
    {
        const string broken = "{ \"hooks\": {\n";
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />", [".claude/settings.json"] = broken });
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(2, InitCommand.Run(repo.Root.Path, output, error));

        Assert.StartsWith("fuse: .claude/settings.json is not valid JSON (", error.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("); fix or remove it" + Environment.NewLine, error.ToString(), StringComparison.Ordinal);
        Assert.Equal(broken, repo.Read(".claude/settings.json"));
        Assert.Equal([repo.Full(".claude/settings.json")], Directory.GetFiles(repo.Full(".claude")));
    }

    [Fact]
    public void A_permission_that_is_not_a_string_is_kept_and_skipped()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
            ["App/App.csproj"] = "<Project />",
            [".claude/settings.json"] = """{ "permissions": { "allow": [ 7, "Bash(dotnet build:*)" ] } }""",
        });
        Assert.Equal(0, Init(repo));
        var allow = Json(repo, ".claude/settings.json")["permissions"]!["allow"]!.AsArray().Select(r => r!.ToJsonString()).ToList();
        Assert.Equal(["7", "\"Bash(dotnet build:*)\"", "\"Bash(fuse build:*)\""], allow);
    }

    [Fact]
    public void A_settings_file_that_cannot_be_replaced_is_named_and_no_temporary_file_is_left()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["App/App.csproj"] = "<Project />", [".claude/settings.json"] = "{}" });
        var settings = repo.Full(".claude/settings.json");
        File.SetAttributes(settings, FileAttributes.ReadOnly);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(repo.Full(".claude"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            Assert.Equal(2, InitCommand.Run(repo.Root.Path, output, error));

            Assert.StartsWith("fuse: could not write .claude/settings.json (", error.ToString(), StringComparison.Ordinal);
            Assert.Equal([settings], Directory.GetFiles(repo.Full(".claude")));
            Assert.Equal("{}", repo.Read(".claude/settings.json"));
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(repo.Full(".claude"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetAttributes(settings, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Outside_a_repository_fails_cleanly()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fuse-no-repo-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(directory);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(2, InitCommand.Run(directory, output, error));
            Assert.Contains("not inside a git repository", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
