using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fuse.Cli.Services;

/// <summary>
///     Merges Fuse's ambient-verification hooks (S3) into a project's Claude Code <c>.claude/settings.json</c>: a
///     PostToolUse hook that runs <c>fuse check --delta --fast</c> after every Edit/Write (emitting the diagnostics
///     delta into the transcript) and a Stop hook that runs <c>fuse gate</c> (blocking a turn that ends with
///     introduced errors). The merge is a JSON-DOM operation (not POCO reflection serialization), so it preserves
///     every other setting and hook the file already carries and is idempotent - re-running does not duplicate the
///     Fuse entries.
/// </summary>
public static class ClaudeHooksConfig
{
    /// <summary>The PostToolUse matcher that fires the delta hook on file edits.</summary>
    public const string EditMatcher = "Edit|Write";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    ///     Merges the Fuse ambient-verification hooks into an existing settings document (or a new one).
    /// </summary>
    /// <param name="existingJson">The current <c>.claude/settings.json</c> content, or null/empty for a new file.</param>
    /// <param name="fuseCommand">The Fuse executable the hooks invoke (for example <c>fuse</c> or an absolute path).</param>
    /// <returns>The merged settings JSON, indented.</returns>
    public static string Merge(string? existingJson, string fuseCommand)
    {
        var root = ParseObject(existingJson);
        var hooks = root["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        UpsertCommandHook(hooks, "PostToolUse", EditMatcher, $"{fuseCommand} check --delta --fast");
        UpsertCommandHook(hooks, "Stop", matcher: null, $"{fuseCommand} gate");

        return root.ToJsonString(WriteOptions);
    }

    /// <summary>
    ///     Whether a settings document already carries both Fuse ambient-verification hooks, so a caller can report
    ///     "already installed" rather than rewriting an unchanged file.
    /// </summary>
    /// <param name="existingJson">The current settings content, or null.</param>
    /// <returns><see langword="true" /> when both the PostToolUse delta hook and the Stop gate hook are present.</returns>
    public static bool AlreadyInstalled(string? existingJson)
    {
        if (string.IsNullOrWhiteSpace(existingJson))
            return false;

        var hooks = ParseObject(existingJson)["hooks"] as JsonObject;
        if (hooks is null)
            return false;

        return HasFuseCommand(hooks, "PostToolUse", "check --delta")
            && HasFuseCommand(hooks, "Stop", "gate");
    }

    private static JsonObject ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // A malformed settings file is not overwritten blindly by the pure merge; the caller decides. Here we
            // start from empty so the merge produces valid output the caller can compare or reject.
            return new JsonObject();
        }
    }

    // Ensures the named event array holds a matcher group whose hooks include the Fuse command. Idempotent: if a
    // hook whose command marks it as this Fuse hook already exists, nothing is added.
    private static void UpsertCommandHook(JsonObject hooks, string eventName, string? matcher, string command)
    {
        var marker = MarkerFor(command);
        if (hooks[eventName] is not JsonArray group)
        {
            group = new JsonArray();
            hooks[eventName] = group;
        }

        if (ContainsFuseCommand(group, marker))
            return;

        var hookEntry = new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
            }),
        };
        if (matcher is not null)
            hookEntry["matcher"] = matcher;

        group.Add(hookEntry);
    }

    private static bool HasFuseCommand(JsonObject hooks, string eventName, string marker) =>
        hooks[eventName] is JsonArray group && ContainsFuseCommand(group, marker);

    // A matcher group entry contains the Fuse command when any of its inner command hooks carries the marker
    // substring (so a path-qualified fuse command still dedups against a bare one).
    private static bool ContainsFuseCommand(JsonArray group, string marker)
    {
        foreach (var entry in group)
        {
            if (entry is not JsonObject obj || obj["hooks"] is not JsonArray inner)
                continue;
            foreach (var hook in inner)
            {
                if (hook is JsonObject hookObj
                    && hookObj["command"]?.GetValue<string>() is { } command
                    && command.Contains(marker, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    // The distinguishing substring of a Fuse hook command, used to dedup regardless of the executable path prefix.
    private static string MarkerFor(string command) =>
        command.Contains("check --delta", StringComparison.Ordinal) ? "check --delta" : "gate";
}
