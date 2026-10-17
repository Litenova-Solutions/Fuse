using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     Muse Code. It runs hooks only from plugin bundles, so its registration is the bundle <c>.muse/plugins/fuse</c>, whose
///     manifest <c>.muse-plugin/plugin.json</c> belongs to Fuse and is written whole. Muse loads the bundle once you install
///     it in the project and approve its hooks (<see cref="NextStep"/>). Its payloads and answers follow Claude Code's
///     shape; its edit tools name the file in <c>path</c>, relative to the working directory, and its shell tool, whatever
///     its name on the platform, carries <c>command</c>.
/// </summary>
/// <remarks>
///     A plugin hook runs for every tool of its event, with no matcher, so the post-edit hook runs after every tool and
///     <c>fuse hook</c> answers only when the tool wrote a C# source. A subagent runs in a session of its own whose id does
///     not name its parent, so its edits are its own session's. Its <c>SubagentStop</c> event also fires for Muse's
///     internal helpers, so Fuse registers only the main session's <c>Stop</c>.
/// </remarks>
internal sealed class MuseCode : Harness
{
    /// <summary>The bundle's folder, relative to the repository root.</summary>
    internal const string BundleFolder = ".muse/plugins/fuse";

    public override string Name => "muse";

    /// <summary>
    ///     Muse rejects <c>updatedInput</c> without an allow decision, which runs the command without asking the user, so
    ///     only a command that is one plain invocation is rewritten.
    /// </summary>
    public override bool ApprovesRewrittenCommand => true;

    public override bool IsUsedIn(RepoRoot root) => HasAny(root, ".muse");

    /// <summary>Muse's tools that write files; its post-edit hook also runs after every tool that only reads one.</summary>
    public override bool WritesFiles(string? toolName) => toolName is "edit_file" or "write_file" or "apply_patch";

    /// <summary>What to run once after <c>fuse init</c>, so Muse loads the bundle.</summary>
    public override string? NextStep =>
        $"Muse Code loads hooks from an installed plugin: run `muse plugins install {BundleFolder} --scope project` and `muse plugins approve fuse` once, and `muse plugins update fuse` after a later fuse init";

    public override WrittenFile RegisterHooks(RepoRoot root)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["name"] = "fuse",
            ["displayName"] = "Fuse",
            ["version"] = "1.0.0",
            ["description"] = "Compiler errors your edits introduced, affected tests and compact build output for .NET, from fuse.codes.",
            ["compat"] = new JsonObject { ["source"] = "native", ["manifestDir"] = ".muse-plugin" },
            ["capabilities"] = new JsonObject
            {
                ["skills"] = new JsonArray(),
                ["commands"] = new JsonArray(),
                ["hooks"] = new JsonArray(
                    Hook("post-edit", "PostToolUse", HookEvent.PostEdit, 60000, "Checking the edit with Fuse"),
                    Hook("pre-shell", "PreToolUse", HookEvent.PreShell, 10000, null),
                    Hook("stop", "Stop", HookEvent.Stop, 300000, "Checking every change with Fuse")),
                ["mcpServers"] = new JsonArray(),
                ["reminders"] = new JsonArray(),
            },
        };
        var path = Path.Combine(root.Path, BundleFolder, ".muse-plugin", "plugin.json");
        return SettingsFile.Write(root, path, manifest);
    }

    private JsonObject Hook(string id, string museEvent, string hookEvent, int timeoutMs, string? status)
    {
        var hook = new JsonObject
        {
            ["id"] = id,
            ["event"] = museEvent,
            ["command"] = new JsonArray("fuse", "hook", Name, hookEvent),
            ["timeoutMs"] = timeoutMs,
        };
        if (status is not null)
            hook["statusMessage"] = status;
        return hook;
    }

    /// <summary>
    ///     Muse honours <c>updatedInput</c> only together with an allow decision, so this answer approves
    ///     <paramref name="command"/>; <see cref="ApprovesRewrittenCommand"/> keeps it to a command Fuse rewrote whole. The
    ///     tool's other fields, such as its working directory, are carried over.
    /// </summary>
    public override HookAnswer ReplaceShellCommand(JsonElement? toolInput, string command)
    {
        var input = toolInput is { } element ? JsonNode.Parse(element.GetRawText())!.AsObject() : [];
        input["command"] = command;
        return HookAnswer.Json(new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = "allow",
                ["updatedInput"] = input,
            },
        });
    }

    /// <summary>
    ///     Muse shows the model a post-tool <c>reason</c> given with <c>decision: block</c> and goes on with the turn; it does
    ///     not show <c>additionalContext</c> from this event.
    /// </summary>
    public override HookAnswer ReportAfterEdit(string report) =>
        HookAnswer.Json(new JsonObject { ["decision"] = "block", ["reason"] = report });
}
