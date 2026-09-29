using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     OpenCode. It runs JavaScript plugins, not commands, so its registration is <c>.opencode/plugins/fuse.js</c>, the
///     plugin embedded from <c>opencode-plugin.js</c>, which belongs to Fuse and is written whole. The plugin passes each
///     tool event to <c>fuse hook opencode</c> and reads the fields these answers set, so the two change together.
/// </summary>
internal sealed class OpenCode : Harness
{
    private const string PluginResource = "Fuse.Harnesses.opencode-plugin.js";

    public override string Name => "opencode";

    public override bool IsUsedIn(RepoRoot root) => HasAny(root, ".opencode", "opencode.json", "opencode.jsonc");

    public override string RegisterHooks(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".opencode", "plugins", "fuse.js");
        using var plugin = typeof(OpenCode).Assembly.GetManifestResourceStream(PluginResource)!;
        using var reader = new StreamReader(plugin);
        return SettingsFile.WriteText(root, path, reader.ReadToEnd());
    }

    /// <summary>The plugin sets the command on the tool's arguments itself.</summary>
    public override HookAnswer ReplaceShellCommand(JsonElement? toolInput, string command) => HookAnswer.Json(new JsonObject { ["command"] = command });

    /// <summary>The plugin adds the report to the edit tool's output.</summary>
    public override HookAnswer ReportAfterEdit(string report) => HookAnswer.Json(new JsonObject { ["additionalContext"] = report });


}
