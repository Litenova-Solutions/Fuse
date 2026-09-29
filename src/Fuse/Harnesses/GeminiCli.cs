using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>Gemini CLI. Its hooks go in <c>.gemini/settings.json</c>, in the nested format, with timeouts in milliseconds.</summary>
internal sealed class GeminiCli : Harness
{
    public override string Name => "gemini";

    public override bool IsUsedIn(RepoRoot root) => HasAny(root, ".gemini", "GEMINI.md");

    public override string RegisterHooks(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".gemini", "settings.json");
        var settings = SettingsFile.Read(root, path);
        var hooks = SettingsFile.GetOrAddObject(settings, "hooks");
        SetNestedHook(hooks, "AfterTool", "write_file|replace", new JsonObject { ["name"] = "fuse-check", ["type"] = "command", ["command"] = Command(HookEvent.PostEdit), ["timeout"] = 60000 });
        SetNestedHook(hooks, "BeforeTool", "run_shell_command", new JsonObject { ["name"] = "fuse-dotnet", ["type"] = "command", ["command"] = Command(HookEvent.PreShell), ["timeout"] = 10000 });
        SetNestedHook(hooks, "AfterAgent", null, new JsonObject { ["name"] = "fuse-stop", ["type"] = "command", ["command"] = Command(HookEvent.Stop), ["timeout"] = 300000 });
        return SettingsFile.Write(root, path, settings);
    }

    /// <summary>Gemini CLI merges <c>tool_input</c> over the model's arguments, so the answer carries only the command.</summary>
    public override HookAnswer ReplaceShellCommand(JsonElement? toolInput, string command) =>
        HookAnswer.Json(new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "BeforeTool", ["tool_input"] = new JsonObject { ["command"] = command } },
        });

    public override HookAnswer ReportAfterEdit(string report) =>
        HookAnswer.Json(new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "AfterTool", ["additionalContext"] = report } });

    public override HookAnswer AllowStop() => HookAnswer.Json([]);

    /// <summary>Gemini CLI's decision for sending the agent back is <c>deny</c>, where the other harnesses use <c>block</c>.</summary>
    public override HookAnswer BlockStop(string reason) => HookAnswer.Json(new JsonObject { ["decision"] = "deny", ["reason"] = reason });
}
