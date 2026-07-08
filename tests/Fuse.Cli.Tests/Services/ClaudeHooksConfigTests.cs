using System.Text.Json.Nodes;
using Fuse.Cli.Services;
using Xunit;

namespace Fuse.Cli.Tests.Services;

// S3: the idempotent JSON-DOM merge of the ambient-verification hooks into .claude/settings.json. It must add the
// PostToolUse delta hook and the Stop gate hook, preserve every other setting and hook, and not duplicate on a
// re-run.
public sealed class ClaudeHooksConfigTests
{
    [Fact]
    public void Merge_into_empty_adds_both_hooks()
    {
        var json = ClaudeHooksConfig.Merge(null, "fuse");
        var root = JsonNode.Parse(json)!.AsObject();

        var postToolUse = root["hooks"]!["PostToolUse"]!.AsArray();
        var stop = root["hooks"]!["Stop"]!.AsArray();
        Assert.Equal("Edit|Write", postToolUse[0]!["matcher"]!.GetValue<string>());
        Assert.Contains("check --delta --fast", postToolUse[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Contains("gate", stop[0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Merge_preserves_existing_settings_and_hooks()
    {
        const string existing = """
            {
              "model": "sonnet",
              "hooks": {
                "PostToolUse": [
                  { "matcher": "Bash", "hooks": [{ "type": "command", "command": "echo hi" }] }
                ]
              }
            }
            """;

        var json = ClaudeHooksConfig.Merge(existing, "fuse");
        var root = JsonNode.Parse(json)!.AsObject();

        // The unrelated top-level setting and the pre-existing Bash hook both survive.
        Assert.Equal("sonnet", root["model"]!.GetValue<string>());
        var postToolUse = root["hooks"]!["PostToolUse"]!.AsArray();
        Assert.Equal(2, postToolUse.Count); // the Bash hook plus the fuse delta hook
        Assert.Contains(postToolUse, e => e!["matcher"]!.GetValue<string>() == "Bash");
        Assert.Contains(postToolUse, e => e!["matcher"]?.GetValue<string>() == "Edit|Write");
    }

    [Fact]
    public void Merge_is_idempotent()
    {
        var once = ClaudeHooksConfig.Merge(null, "fuse");
        var twice = ClaudeHooksConfig.Merge(once, "fuse");
        var root = JsonNode.Parse(twice)!.AsObject();

        Assert.Single(root["hooks"]!["PostToolUse"]!.AsArray());
        Assert.Single(root["hooks"]!["Stop"]!.AsArray());
    }

    [Fact]
    public void Merge_dedups_against_a_path_qualified_command()
    {
        // A first install used an absolute fuse path; a re-run with a bare `fuse` must still dedup (marker match).
        var first = ClaudeHooksConfig.Merge(null, "/usr/local/bin/fuse");
        var second = ClaudeHooksConfig.Merge(first, "fuse");
        var root = JsonNode.Parse(second)!.AsObject();

        Assert.Single(root["hooks"]!["PostToolUse"]!.AsArray());
        Assert.Single(root["hooks"]!["Stop"]!.AsArray());
    }

    [Fact]
    public void AlreadyInstalled_is_true_only_when_both_hooks_present()
    {
        Assert.False(ClaudeHooksConfig.AlreadyInstalled(null));
        Assert.False(ClaudeHooksConfig.AlreadyInstalled("{}"));
        var merged = ClaudeHooksConfig.Merge(null, "fuse");
        Assert.True(ClaudeHooksConfig.AlreadyInstalled(merged));
    }
}
