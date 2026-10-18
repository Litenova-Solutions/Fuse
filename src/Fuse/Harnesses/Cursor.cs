using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Paths;

namespace Fuse.Harnesses;

/// <summary>
///     Cursor. Its hooks go in <c>.cursor/hooks.json</c>, in a flat format where each event holds a list of handlers
///     with no matcher groups. Fuse registers a post-edit, a pre-shell and a stop hook with it.
/// </summary>
internal sealed class Cursor : Harness
{
    public override string Name => "cursor";

    /// <summary>Cursor replaces a tool's input only in an answer that allows the call, which runs the command it names.</summary>
    public override bool ApprovesRewrittenCommand => true;

    public override bool IsUsedIn(RepoRoot root) => HasAny(root, ".cursor");

    public override WrittenFile RegisterHooks(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".cursor", "hooks.json");
        var settings = SettingsFile.Read(root, path);
        settings["version"] ??= 1;
        var hooks = SettingsFile.GetOrAddObject(settings, "hooks");
        SetFlatHook(hooks, "postToolUse", new JsonObject { ["command"] = Command(HookEvent.PostEdit), ["matcher"] = "Write", ["timeout"] = 60 });
        SetFlatHook(hooks, "preToolUse", new JsonObject { ["command"] = Command(HookEvent.PreShell), ["matcher"] = "Shell", ["timeout"] = 10 });
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

    /// <summary>Cursor takes <c>updated_input</c> only with an allow answer, so this approves the rewritten command; <see cref="ApprovesRewrittenCommand"/> keeps it to a command Fuse rewrote whole. Every field the harness sent is carried over. A command Fuse does not rewrite gets no answer, which lets it through unapproved.</summary>
    public override HookAnswer ReplaceShellCommand(JsonElement? toolInput, string command) =>
        HookAnswer.Json(new JsonObject
        {
            ["permission"] = "allow",
            ["updated_input"] = WithCommand(toolInput, command),
        });

    private static JsonObject WithCommand(JsonElement? input, string command)
    {
        var result = input is { } element ? JsonNode.Parse(element.GetRawText())!.AsObject() : [];
        result["command"] = command;
        return result;
    }

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
