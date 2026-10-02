using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     GitHub Copilot CLI. Its hooks go in <c>.github/hooks/fuse.json</c>, a file that belongs to Fuse and is written
///     whole, with the same command for bash and PowerShell. Fuse registers a post-edit and a stop hook with it, and no
///     pre-shell hook.
/// </summary>
internal sealed class CopilotCli : Harness
{
    public override string Name => "copilot";

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
                ["agentStop"] = new JsonArray(new JsonObject { ["type"] = "command", ["bash"] = Command(HookEvent.Stop), ["powershell"] = Command(HookEvent.Stop), ["timeoutSec"] = 300 }),
            },
        };
        return SettingsFile.Write(root, path, settings);
    }

    /// <summary>Only a pre-shell hook the user registered by hand reaches this, and it gets no answer.</summary>
    public override HookAnswer ReplaceShellCommand(JsonElement? toolInput, string command) => HookAnswer.None;

    public override HookAnswer ReportAfterEdit(string report) => HookAnswer.Json(new JsonObject { ["additionalContext"] = report });


}
