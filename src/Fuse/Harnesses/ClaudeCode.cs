using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     Claude Code. Its hooks go in <c>.claude/settings.json</c>, in the nested format, where <c>fuse init</c> also gives
///     <c>fuse build</c> and <c>fuse test</c> the permissions the user already gave <c>dotnet build</c> and
///     <c>dotnet test</c>.
/// </summary>
internal sealed class ClaudeCode : Harness
{
    public override string Name => "claude";

    /// <summary>The post-edit hook is registered with <c>asyncRewake</c>, so Claude Code runs it in the background.</summary>
    public override bool RunsPostEditInBackground => true;

    /// <summary>Cursor reads <c>.claude/settings.json</c> as well as its own hooks.</summary>
    public override bool IsAlsoRunByCursor => true;

    public override bool IsUsedIn(RepoRoot root) => HasAny(root, ".claude", "CLAUDE.md");

    public override string RegisterHooks(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".claude", "settings.json");
        var settings = SettingsFile.Read(root, path);
        var hooks = SettingsFile.GetOrAddObject(settings, "hooks");
        SetNestedHook(hooks, "PostToolUse", "Edit|Write|MultiEdit", new JsonObject { ["type"] = "command", ["command"] = Command(HookEvent.PostEdit), ["asyncRewake"] = true, ["timeout"] = 300 });
        SetNestedHook(hooks, "PreToolUse", "Bash", new JsonObject { ["type"] = "command", ["command"] = Command(HookEvent.PreShell), ["timeout"] = 10 });
        SetNestedHook(hooks, "Stop", null, new JsonObject { ["type"] = "command", ["command"] = Command(HookEvent.Stop), ["timeout"] = 300 });

        // A user who already lets the agent run dotnet build/test without asking gets the same for the rewritten commands.
        if (settings["permissions"]?["allow"] is JsonArray allow)
        {
            // An entry that is not a string is the user's to keep; it matches no rule.
            var rules = allow.Select(r => r is JsonValue value && value.TryGetValue<string>(out var rule) ? rule : "").ToList();
            foreach (var verb in new[] { "build", "test" })
            {
                if (rules.Any(r => r is "Bash(dotnet:*)" or "Bash(dotnet *)" || r.StartsWith($"Bash(dotnet {verb}", StringComparison.Ordinal))
                    && !rules.Contains($"Bash(fuse {verb}:*)"))
                    allow.Add($"Bash(fuse {verb}:*)");
            }
        }

        return SettingsFile.Write(root, path, settings);
    }

    /// <summary>
    ///     <c>updatedInput</c> replaces the whole tool input, so every field the harness sent is carried over. The answer
    ///     makes no permission decision: the rewritten command goes through the user's normal permission rules.
    /// </summary>
    public override HookAnswer ReplaceShellCommand(JsonElement? toolInput, string command) =>
        HookAnswer.Json(new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["updatedInput"] = WithCommand(toolInput, command),
            },
        });

    /// <summary>With <c>asyncRewake</c>, exit code 2 wakes the agent and shows standard error to it as a system reminder.</summary>
    public override HookAnswer ReportAfterEdit(string report) => new("", report, 2);

    public override HookAnswer AllowStop() => HookAnswer.None;

    public override HookAnswer BlockStop(string reason) => HookAnswer.Json(new JsonObject { ["decision"] = "block", ["reason"] = reason });

    private static JsonObject WithCommand(JsonElement? input, string command)
    {
        var result = input is { } element ? JsonNode.Parse(element.GetRawText())!.AsObject() : [];
        result["command"] = command;
        return result;
    }
}
