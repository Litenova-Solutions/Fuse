using System.Text.Json;
using System.Text.Json.Nodes;
using Fuse.Repo;

namespace Fuse.Hooks;

/// <summary>
///     <c>fuse init</c>: registers Fuse's hooks with every agent harness the repository already uses, and the MCP
///     server with VS Code, which has no hooks. Fuse entries in shared settings files are replaced and other entries kept; <c>.github/hooks/fuse.json</c> and <c>.opencode/plugins/fuse.js</c> belong to Fuse and are written whole.
/// </summary>
internal static class InitCommand
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Run() => Run(Environment.CurrentDirectory, Console.Out, Console.Error);

    /// <summary>Installs hooks for the repository containing <paramref name="startDirectory"/>.</summary>
    internal static int Run(string startDirectory, TextWriter output, TextWriter error)
    {
        var root = RepoRoot.Find(startDirectory);
        if (root is null)
        {
            error.WriteLine("fuse: not inside a git repository; run fuse init from your repository");
            return 2;
        }

        if (!RepoProbe.HasCSharpProjectsAsync(root, CancellationToken.None).GetAwaiter().GetResult())
        {
            error.WriteLine("fuse: no C# projects (.csproj) in this repository; Fuse installs hooks only where there is C# to check");
            return 2;
        }

        var written = new List<string>();
        bool Has(params string[] paths) => paths.Any(p => Directory.Exists(Path.Combine(root.Path, p)) || File.Exists(Path.Combine(root.Path, p)));

        var claude = Has(".claude", "CLAUDE.md");
        var cursor = Has(".cursor");
        var gemini = Has(".gemini", "GEMINI.md");
        var codex = Has(".codex");
        var copilot = Has(".github/copilot-instructions.md", ".github/hooks");
        var opencode = Has(".opencode", "opencode.json", "opencode.jsonc");
        var vscode = Has(".vscode");
        if (!claude && !cursor && !gemini && !codex && !copilot && !opencode && !vscode)
            claude = true;

        if (claude)
            written.Add(WriteClaude(root));
        if (cursor)
            written.Add(WriteCursor(root));
        if (gemini)
            written.Add(WriteGemini(root));
        if (codex)
            written.Add(WriteCodex(root));
        if (copilot)
            written.Add(WriteCopilot(root));
        if (opencode)
            written.Add(WriteOpenCode(root));
        if (vscode)
            written.Add(WriteVsCode(root));

        foreach (var file in written)
            output.WriteLine($"wrote {file}");
        output.WriteLine("fuse: hooks installed; your agent gets compiler errors after each edit, affected tests for `dotnet test`, and compact `dotnet build` output");
        return 0;
    }

    private static string WriteClaude(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".claude", "settings.json");
        var settings = Load(path);
        var hooks = Object(settings, "hooks");
        SetHook(hooks, "PostToolUse", "Edit|Write|MultiEdit", new JsonObject { ["type"] = "command", ["command"] = "fuse hook claude post-edit", ["asyncRewake"] = true, ["timeout"] = 300 });
        SetHook(hooks, "PreToolUse", "Bash", new JsonObject { ["type"] = "command", ["command"] = "fuse hook claude pre-bash", ["timeout"] = 10 });
        SetHook(hooks, "Stop", null, new JsonObject { ["type"] = "command", ["command"] = "fuse hook claude stop", ["timeout"] = 300 });

        // A user who already lets the agent run dotnet build/test without asking gets the same for the rewritten commands.
        if (settings["permissions"]?["allow"] is JsonArray allow)
        {
            var rules = allow.Select(r => r?.GetValue<string>() ?? "").ToList();
            foreach (var verb in new[] { "build", "test" })
            {
                if (rules.Any(r => r is "Bash(dotnet:*)" or "Bash(dotnet *)" || r.StartsWith($"Bash(dotnet {verb}", StringComparison.Ordinal))
                    && !rules.Contains($"Bash(fuse {verb}:*)"))
                    allow.Add($"Bash(fuse {verb}:*)");
            }
        }

        return Save(root, path, settings);
    }

    private static string WriteCursor(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".cursor", "hooks.json");
        var settings = Load(path);
        settings["version"] ??= 1;
        var hooks = Object(settings, "hooks");
        SetFlatHook(hooks, "postToolUse", new JsonObject { ["command"] = "fuse hook cursor post-edit", ["matcher"] = "Write", ["timeout"] = 60 });
        SetFlatHook(hooks, "stop", new JsonObject { ["command"] = "fuse hook cursor stop", ["timeout"] = 300 });
        return Save(root, path, settings);
    }

    private static string WriteGemini(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".gemini", "settings.json");
        var settings = Load(path);
        var hooks = Object(settings, "hooks");
        // Gemini timeouts are in milliseconds.
        SetHook(hooks, "AfterTool", "write_file|replace", new JsonObject { ["name"] = "fuse-check", ["type"] = "command", ["command"] = "fuse hook gemini post-edit", ["timeout"] = 60000 });
        SetHook(hooks, "BeforeTool", "run_shell_command", new JsonObject { ["name"] = "fuse-dotnet", ["type"] = "command", ["command"] = "fuse hook gemini pre-bash", ["timeout"] = 10000 });
        SetHook(hooks, "AfterAgent", null, new JsonObject { ["name"] = "fuse-stop", ["type"] = "command", ["command"] = "fuse hook gemini stop", ["timeout"] = 300000 });
        return Save(root, path, settings);
    }

    private static string WriteCodex(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".codex", "hooks.json");
        var settings = Load(path);
        var hooks = Object(settings, "hooks");
        SetHook(hooks, "PostToolUse", "^apply_patch$", new JsonObject { ["type"] = "command", ["command"] = "fuse hook codex post-edit", ["timeout"] = 60 });
        SetHook(hooks, "PreToolUse", "^Bash$", new JsonObject { ["type"] = "command", ["command"] = "fuse hook codex pre-bash", ["timeout"] = 10 });
        SetHook(hooks, "Stop", null, new JsonObject { ["type"] = "command", ["command"] = "fuse hook codex stop", ["timeout"] = 300 });
        return Save(root, path, settings);
    }

    private static string WriteCopilot(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".github", "hooks", "fuse.json");
        var settings = new JsonObject
        {
            ["version"] = 1,
            ["hooks"] = new JsonObject
            {
                ["postToolUse"] = new JsonArray(new JsonObject { ["type"] = "command", ["matcher"] = "edit|create", ["bash"] = "fuse hook copilot post-edit", ["powershell"] = "fuse hook copilot post-edit", ["timeoutSec"] = 60 }),
                ["agentStop"] = new JsonArray(new JsonObject { ["type"] = "command", ["bash"] = "fuse hook copilot stop", ["powershell"] = "fuse hook copilot stop", ["timeoutSec"] = 300 }),
            },
        };
        return Save(root, path, settings);
    }

    /// <summary>OpenCode runs JavaScript plugins, not commands; the plugin forwards its tool events to <c>fuse hook opencode</c>.</summary>
    private static string WriteOpenCode(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".opencode", "plugins", "fuse.js");
        using var plugin = typeof(InitCommand).Assembly.GetManifestResourceStream("Fuse.Hooks.opencode-plugin.js")!;
        using var reader = new StreamReader(plugin);
        return SaveText(root, path, reader.ReadToEnd());
    }

    private static string WriteVsCode(RepoRoot root)
    {
        var path = Path.Combine(root.Path, ".vscode", "mcp.json");
        var settings = Load(path);
        var servers = Object(settings, "servers");
        servers["fuse"] = new JsonObject { ["type"] = "stdio", ["command"] = "fuse", ["args"] = new JsonArray("mcp"), ["cwd"] = "${workspaceFolder}" };
        return Save(root, path, settings);
    }

    /// <summary>Replaces Fuse's entry for <paramref name="hookEvent"/> in the Claude-style nested format (matcher groups holding handler lists).</summary>
    private static void SetHook(JsonObject hooks, string hookEvent, string? matcher, JsonObject handler)
    {
        var groups = hooks[hookEvent] as JsonArray ?? [];
        hooks[hookEvent] = groups;
        foreach (var group in groups.OfType<JsonObject>().ToList())
        {
            if (group["hooks"] is JsonArray handlers)
            {
                foreach (var existing in handlers.OfType<JsonObject>().Where(IsFuse).ToList())
                    handlers.Remove(existing);
                if (handlers.Count == 0)
                    groups.Remove(group);
            }
        }

        var entry = new JsonObject { ["hooks"] = new JsonArray(handler) };
        if (matcher is not null)
            entry.Insert(0, "matcher", matcher);
        groups.Add(entry);
    }

    /// <summary>Replaces Fuse's entry for <paramref name="hookEvent"/> in Cursor's flat format (a list of handlers).</summary>
    private static void SetFlatHook(JsonObject hooks, string hookEvent, JsonObject handler)
    {
        var handlers = hooks[hookEvent] as JsonArray ?? [];
        hooks[hookEvent] = handlers;
        foreach (var existing in handlers.OfType<JsonObject>().Where(IsFuse).ToList())
            handlers.Remove(existing);
        handlers.Add(handler);
    }

    private static bool IsFuse(JsonObject handler) =>
        handler["command"]?.GetValueKind() == JsonValueKind.String && handler["command"]!.GetValue<string>().StartsWith("fuse hook", StringComparison.Ordinal);

    private static JsonObject Object(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
            return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path))
            return [];
        var text = File.ReadAllText(path);
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return node as JsonObject ?? [];
    }

    private static string Save(RepoRoot root, string path, JsonObject content) =>
        SaveText(root, path, content.ToJsonString(Indented) + Environment.NewLine);

    private static string SaveText(RepoRoot root, string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".fuse-tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
        return root.Relative(path);
    }
}
