using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fuse.Hooks;

/// <summary>The fields Fuse needs from a harness's hook input, read leniently because each harness names them differently.</summary>
internal sealed partial class HookPayload
{
    private readonly JsonElement _root;

    private HookPayload(JsonElement root) => _root = root;

    public static HookPayload Parse(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return new HookPayload(document.RootElement.Clone());
    }

    /// <summary>The working directory the harness reports, or the process's own.</summary>
    public string Cwd => String("cwd") ?? String("workspace_roots", 0) ?? Environment.CurrentDirectory;

    /// <summary>True when Cursor runs a hook it loaded from Claude Code's settings.</summary>
    public bool FromCursor => _root.TryGetProperty("cursor_version", out _);

    /// <summary>True when the harness is already continuing because of an earlier Stop hook block.</summary>
    public bool StopHookActive =>
        (_root.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.True)
        || (_root.TryGetProperty("loop_count", out var loops) && loops.ValueKind == JsonValueKind.Number && loops.GetInt32() > 0);

    /// <summary>The tool input object (<c>tool_input</c>, or Copilot's <c>toolArgs</c>, which may be a JSON string).</summary>
    public JsonElement? ToolInput
    {
        get
        {
            foreach (var name in new[] { "tool_input", "toolArgs", "toolInput" })
            {
                if (!_root.TryGetProperty(name, out var value))
                    continue;
                if (value.ValueKind == JsonValueKind.Object)
                    return value;
                if (value.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        using var parsed = JsonDocument.Parse(value.GetString()!);
                        return parsed.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                }
            }

            return null;
        }
    }

    /// <summary>The shell command of a Bash-like tool call.</summary>
    public string? Command => ToolInput is { } input && input.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : String("command");

    /// <summary>Absolute paths of the files an edit tool wrote, including files named in a Codex <c>apply_patch</c> patch.</summary>
    public IReadOnlyList<string> EditedFiles()
    {
        var paths = new List<string>();
        if (ToolInput is { } input)
        {
            foreach (var name in new[] { "file_path", "filePath", "path", "notebook_path" })
            {
                if (input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    paths.Add(value.GetString()!);
            }

            if (input.TryGetProperty("command", out var patch) && patch.ValueKind == JsonValueKind.String)
                paths.AddRange(PatchFiles(patch.GetString()!));
        }

        if (String("file_path") is { } topLevel)
            paths.Add(topLevel);
        var cwd = Cwd;
        return paths.Select(p => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(cwd, p))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Files an apply_patch patch adds, updates, deletes or moves to.</summary>
    internal static IEnumerable<string> PatchFiles(string patch) =>
        PatchHeader().Matches(patch).Select(m => m.Groups["path"].Value.Trim());

    private string? String(string name, int index = -1)
    {
        if (!_root.TryGetProperty(name, out var value))
            return null;
        if (index >= 0)
            return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > index && value[index].ValueKind == JsonValueKind.String ? value[index].GetString() : null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    [GeneratedRegex(@"^\*\*\* (?:Add File|Update File|Delete File|Move to): (?<path>.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PatchHeader();
}
