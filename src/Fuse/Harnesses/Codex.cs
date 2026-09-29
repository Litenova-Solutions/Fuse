using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>Codex. Its hooks go in <c>.codex/hooks.json</c>, in the nested format, and its edit tool is <c>apply_patch</c>.</summary>
internal sealed class Codex : Harness
{
    public override string Name => "codex";

    /// <summary>Codex honours <c>updatedInput</c> only together with an allow decision, which runs the command without asking the user.</summary>
    public override bool ApprovesRewrittenCommand => true;

    public override bool IsUsedIn(RepoRoot root) => HasAny(root, ".codex");

    public override string RegisterHooks(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".codex", "hooks.json");
        var settings = SettingsFile.Read(root, path);
        var hooks = SettingsFile.GetOrAddObject(settings, "hooks");
        SetNestedHook(hooks, "PostToolUse", "^apply_patch$", new JsonObject { ["type"] = "command", ["command"] = Command(HookEvent.PostEdit), ["timeout"] = 60 });
        SetNestedHook(hooks, "PreToolUse", "^Bash$", new JsonObject { ["type"] = "command", ["command"] = Command(HookEvent.PreShell), ["timeout"] = 10 });
        SetNestedHook(hooks, "Stop", null, new JsonObject { ["type"] = "command", ["command"] = Command(HookEvent.Stop), ["timeout"] = 300 });
        return SettingsFile.Write(root, path, settings);
    }

    /// <summary>
    ///     Codex honours <c>updatedInput</c> only together with an allow decision, so this answer approves
    ///     <paramref name="command"/>; <see cref="ApprovesRewrittenCommand"/> keeps it to a command Fuse rewrote whole.
    /// </summary>
    public override HookAnswer ReplaceShellCommand(JsonElement? toolInput, string command) =>
        HookAnswer.Json(new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "allow",
                ["updatedInput"] = new JsonObject { ["command"] = command },
            },
        });

    public override HookAnswer ReportAfterEdit(string report) =>
        HookAnswer.Json(new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PostToolUse", ["additionalContext"] = report } });


}
