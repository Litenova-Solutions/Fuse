using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     GitHub Copilot CLI. Its hooks go in <c>.github/hooks/fuse.json</c>, a file that belongs to Fuse and is written
///     whole, with the same command for bash and PowerShell.
///     Fuse registers a post-edit, a pre-shell and a stop hook with it.
/// </summary>
internal sealed class CopilotCli : Harness
{
    public override string Name => "copilot";

    /// <summary>Copilot CLI substitutes a tool's arguments only in an answer that allows the call, which runs the command it names.</summary>
    public override bool ApprovesRewrittenCommand => true;

    public override bool IsUsedIn(RepoRoot root) => HasAny(root, ".github/copilot-instructions.md", ".github/hooks");

    public override WrittenFile RegisterHooks(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".github", "hooks", "fuse.json");
        var settings = new JsonObject
        {
            ["version"] = 1,
            ["hooks"] = new JsonObject
            {
                ["postToolUse"] = new JsonArray(new JsonObject { ["type"] = "command", ["matcher"] = "edit|create", ["bash"] = Command(HookEvent.PostEdit), ["powershell"] = Command(HookEvent.PostEdit), ["timeoutSec"] = 60 }),
                ["preToolUse"] = new JsonArray(new JsonObject { ["type"] = "command", ["matcher"] = "bash|powershell", ["bash"] = Command(HookEvent.PreShell), ["powershell"] = Command(HookEvent.PreShell), ["timeoutSec"] = 10 }),
                ["agentStop"] = new JsonArray(new JsonObject { ["type"] = "command", ["bash"] = Command(HookEvent.Stop), ["powershell"] = Command(HookEvent.Stop), ["timeoutSec"] = LongHookSeconds }),
            },
        };
        return SettingsFile.Write(root, path, settings);
    }

    /// <summary>The file belongs to Fuse, so it is deleted whole.</summary>
    public override WrittenFile? RemoveHooks(RepoRoot root) => SettingsFile.Delete(root, Path.Combine(root.Path, ".github", "hooks", "fuse.json"));

    /// <summary>Copilot CLI substitutes arguments only with an allow answer, so this approves the rewritten command; <see cref="ApprovesRewrittenCommand"/> keeps it to a command Fuse rewrote whole. Every field the harness sent is carried over. A command Fuse does not rewrite gets no answer, which uses the default behavior.</summary>
    public override HookAnswer ReplaceShellCommand(JsonElement? toolInput, string command) =>
        HookAnswer.Json(new JsonObject
        {
            ["permissionDecision"] = "allow",
            ["modifiedArgs"] = WithCommand(toolInput, command),
        });

    private static JsonObject WithCommand(JsonElement? input, string command)
    {
        var result = input is { } element ? JsonNode.Parse(element.GetRawText())!.AsObject() : [];
        result["command"] = command;
        return result;
    }

    public override HookAnswer ReportAfterEdit(string report) => HookAnswer.Json(new JsonObject { ["additionalContext"] = report });


}
