using System.Text.Json.Nodes;
using Fuse.Hooks;
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
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        Assert.Equal(0, Init(repo));
        var hooks = Json(repo, ".claude/settings.json")["hooks"]!;
        var post = hooks["PostToolUse"]![0]!;
        Assert.Equal("Edit|Write|MultiEdit", post["matcher"]!.GetValue<string>());
        Assert.Equal("fuse hook claude post-edit", post["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.True(post["hooks"]![0]!["asyncRewake"]!.GetValue<bool>());
        Assert.Equal("fuse hook claude pre-bash", hooks["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("fuse hook claude stop", hooks["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.False(File.Exists(repo.Full(".cursor/hooks.json")));
    }

    [Fact]
    public void Preserves_existing_settings_and_is_idempotent()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
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
    public void Does_not_add_permissions_the_user_had_not_granted()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string>
        {
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
            [".cursor/rules.md"] = "x",
            [".gemini/settings.json"] = "{}",
            [".codex/config.toml"] = "",
            [".github/copilot-instructions.md"] = "x",
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

        var codex = Json(repo, ".codex/hooks.json")["hooks"]!;
        Assert.Equal("^apply_patch$", codex["PostToolUse"]![0]!["matcher"]!.GetValue<string>());

        var copilot = Json(repo, ".github/hooks/fuse.json")["hooks"]!;
        Assert.Equal("fuse hook copilot post-edit", copilot["postToolUse"]![0]!["bash"]!.GetValue<string>());

        var vscode = Json(repo, ".vscode/mcp.json")["servers"]!["fuse"]!;
        Assert.Equal("fuse", vscode["command"]!.GetValue<string>());
        Assert.Equal("mcp", vscode["args"]![0]!.GetValue<string>());
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
