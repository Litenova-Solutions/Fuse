using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     Cursor. Its hooks go in <c>.cursor/hooks.json</c>, in a flat format where each event holds a list of handlers
///     with no matcher groups. Fuse registers a post-edit and a stop hook with it, and no pre-shell hook.
/// </summary>
internal sealed class Cursor : Harness
{
    public override string Name => "cursor";

    public override bool IsUsedIn(RepoRoot root) => HasAny(root, ".cursor");

    public override WrittenFile RegisterHooks(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".cursor", "hooks.json");
        var settings = SettingsFile.Read(root, path);
        settings["version"] ??= 1;
        var hooks = SettingsFile.GetOrAddObject(settings, "hooks");
        SetFlatHook(hooks, "postToolUse", new JsonObject { ["command"] = Command(HookEvent.PostEdit), ["matcher"] = "Write", ["timeout"] = 60 });
        SetFlatHook(hooks, "stop", new JsonObject { ["command"] = Command(HookEvent.Stop), ["timeout"] = LongHookSeconds });
        return SettingsFile.Write(root, path, settings);
    }

    /// <summary>A file left with only the <c>version</c> that <see cref="RegisterHooks"/> adds holds nothing else, so it is deleted.</summary>
    public override WrittenFile? RemoveHooks(RepoRoot root) =>
        RemoveFromSettings(root, Path.Combine(".cursor", "hooks.json"), settings =>
        {
            if (settings["hooks"] is not JsonObject hooks)
                return false;
            var removed = false;
            foreach (var (harnessEvent, value) in hooks.ToList())
            {
                if (value is not JsonArray handlers)
                    continue;
                var fuse = handlers.OfType<JsonObject>().Where(IsFuse).ToList();
                foreach (var handler in fuse)
                    handlers.Remove(handler);
                if (fuse.Count > 0 && handlers.Count == 0)
                    hooks.Remove(harnessEvent);
                removed |= fuse.Count > 0;
            }

            if (removed && hooks.Count == 0)
            {
                settings.Remove("hooks");
                if (settings.Count == 1 && settings.ContainsKey("version"))
                    settings.Remove("version");
            }

            return removed;
        });

    /// <summary>Only a pre-shell hook the user registered by hand reaches this, and it gets no answer.</summary>
    public override HookAnswer ReplaceShellCommand(JsonElement? toolInput, string command) => HookAnswer.None;

    public override HookAnswer ReportAfterEdit(string report) => HookAnswer.Json(new JsonObject { ["additional_context"] = report });


    /// <summary>Cursor sends <c>followup_message</c> to the agent as its next message.</summary>
    public override HookAnswer BlockStop(string reason) => HookAnswer.Json(new JsonObject { ["followup_message"] = reason });

    /// <summary>Replaces Fuse's handler for <paramref name="harnessEvent"/> in the flat format, keeping every other handler.</summary>
    private static void SetFlatHook(JsonObject hooks, string harnessEvent, JsonObject handler)
    {
        var handlers = hooks[harnessEvent] as JsonArray ?? [];
        hooks[harnessEvent] = handlers;
        foreach (var existing in handlers.OfType<JsonObject>().Where(IsFuse).ToList())
            handlers.Remove(existing);
        handlers.Add((JsonNode)handler);
    }
}
