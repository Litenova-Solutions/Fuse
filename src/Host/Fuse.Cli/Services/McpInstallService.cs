using System.Diagnostics;
using System.Text.Json;
using Fuse.Cli.Configuration.McpInstall;
using Fuse.Cli.Serialization;

namespace Fuse.Cli.Services;

/// <summary>
///     Writes MCP client configuration so an AI client can launch <c>fuse mcp serve</c> automatically.
/// </summary>
public sealed class McpInstallService
{
    private const string ServerName = "fuse";

    // The client launches `fuse mcp serve`; both tokens are passed as the stdio command arguments.
    private static readonly string[] ServeArguments = ["mcp", "serve"];

    /// <summary>
    ///     Registers Fuse with the requested MCP clients at the given scope.
    /// </summary>
    /// <param name="clients">The clients to configure.</param>
    /// <param name="scope">Project-local files or user-global registration.</param>
    /// <param name="projectDirectory">The project root for project scope; defaults to the current directory.</param>
    /// <param name="fuseCommand">The executable the client should launch; defaults to the running binary or <c>fuse</c>.</param>
    /// <param name="writeRules">
    ///     When <see langword="true" />, also writes a rule biasing the agent toward the <c>fuse_*</c> tools into
    ///     each client's instruction file. Rule files are project-scoped; under user scope only Claude has a
    ///     global equivalent and the others are skipped with a note.
    /// </param>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <param name="cancellationToken">A token that cancels Claude CLI registration.</param>
    /// <returns>The number of clients configured successfully.</returns>
    public async Task<int> InstallAsync(
        IReadOnlyList<McpInstallClient> clients,
        McpInstallScope scope,
        string? projectDirectory,
        string? fuseCommand,
        bool writeRules,
        IConsoleUI consoleUI,
        CancellationToken cancellationToken)
    {
        if (!TryValidateFuseCommand(fuseCommand, out var command, out var validationError))
        {
            consoleUI.WriteError(validationError!);
            return 0;
        }

        var projectRoot = string.IsNullOrWhiteSpace(projectDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(projectDirectory);

        var configured = 0;
        foreach (var client in clients)
        {
            var success = client switch
            {
                McpInstallClient.Claude => scope == McpInstallScope.User
                    ? await RegisterClaudeUserAsync(command, consoleUI, cancellationToken)
                    : WriteClaudeProjectConfig(projectRoot, command, consoleUI),
                McpInstallClient.Cursor => WriteCursorConfig(scope, projectRoot, command, consoleUI),
                McpInstallClient.Copilot => WriteCopilotConfig(scope, projectRoot, command, consoleUI),
                _ => false,
            };

            if (success)
                configured++;
        }

        if (writeRules)
            foreach (var client in clients)
                WriteClientRule(client, scope, projectRoot, consoleUI);

        return configured;
    }

    /// <summary>
    ///     Resolves the Fuse executable path for MCP registration.
    /// </summary>
    /// <returns>The current process path when available; otherwise <c>fuse</c>.</returns>
    internal static string ResolveFuseCommand()
    {
        var processPath = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(processPath) ? "fuse" : processPath;
    }

    /// <summary>
    ///     Validates a caller-supplied executable path for MCP registration.
    /// </summary>
    /// <param name="fuseCommand">The raw <c>--command</c> value, or <see langword="null" /> to resolve the default.</param>
    /// <param name="command">The sanitized executable path or name.</param>
    /// <param name="errorMessage">A user-facing error when validation fails.</param>
    /// <returns><see langword="true" /> when <paramref name="command" /> is safe to register.</returns>
    internal static bool TryValidateFuseCommand(string? fuseCommand, out string command, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(fuseCommand))
        {
            command = ResolveFuseCommand();
            errorMessage = null;
            return true;
        }

        command = fuseCommand.Trim();

        if (command.AsSpan().ContainsAny("\r\n\0".AsSpan()))
        {
            errorMessage = "Invalid --command: control characters and newlines are not allowed.";
            return false;
        }

        // A single executable path or name; arguments belong in the MCP server's args list, not the command field.
        if (command.IndexOfAny(['|', '&', ';', '`', '$', '<', '>']) >= 0)
        {
            errorMessage = "Invalid --command: shell metacharacters are not allowed. Pass a single executable path.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    private static bool WriteClaudeProjectConfig(string projectRoot, string fuseCommand, IConsoleUI consoleUI)
    {
        var path = GetConfigPath(McpInstallClient.Claude, McpInstallScope.Project, projectRoot);
        var config = LoadOrCreateClaude(path);
        config.McpServers ??= new Dictionary<string, ClaudeMcpServer>();
        config.McpServers[ServerName] = CreateClaudeServer(fuseCommand);
        WriteClaudeConfig(path, config);
        consoleUI.WriteSuccess($"Registered Fuse with Claude Code (project): {path}");
        return true;
    }

    private static bool WriteCursorConfig(
        McpInstallScope scope,
        string projectRoot,
        string fuseCommand,
        IConsoleUI consoleUI)
    {
        var path = GetConfigPath(McpInstallClient.Cursor, scope, projectRoot);

        var config = LoadOrCreateCursor(path);
        config.McpServers ??= new Dictionary<string, CursorMcpServer>();
        config.McpServers[ServerName] = CreateCursorServer(fuseCommand);
        WriteCursorConfigFile(path, config);
        consoleUI.WriteSuccess($"Registered Fuse with Cursor ({DescribeScope(scope)}): {path}");
        return true;
    }

    private static bool WriteCopilotConfig(
        McpInstallScope scope,
        string projectRoot,
        string fuseCommand,
        IConsoleUI consoleUI)
    {
        var path = GetConfigPath(McpInstallClient.Copilot, scope, projectRoot);

        var config = LoadOrCreateCopilot(path);
        config.Servers ??= new Dictionary<string, CopilotMcpServer>();
        config.Servers[ServerName] = CreateCopilotServer(fuseCommand);
        WriteCopilotConfigFile(path, config);
        consoleUI.WriteSuccess($"Registered Fuse with GitHub Copilot ({DescribeScope(scope)}): {path}");
        return true;
    }

    private static async Task<bool> RegisterClaudeUserAsync(
        string fuseCommand,
        IConsoleUI consoleUI,
        CancellationToken cancellationToken)
    {
        var claudePath = FindExecutableOnPath("claude");
        if (claudePath is null)
        {
            consoleUI.WriteError(
                "Claude Code CLI not found on PATH. Install Claude Code, then run: claude mcp add fuse --scope user -- fuse mcp serve");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add("add");
        startInfo.ArgumentList.Add(ServerName);
        startInfo.ArgumentList.Add("--scope");
        startInfo.ArgumentList.Add("user");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(fuseCommand);
        foreach (var argument in ServeArguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            consoleUI.WriteError("Failed to start the Claude Code CLI for MCP registration.");
            return false;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode == 0)
        {
            consoleUI.WriteSuccess("Registered Fuse with Claude Code (user scope, all projects).");
            return true;
        }

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        if (detail.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            consoleUI.WriteStep("Fuse is already registered with Claude Code (user scope).");
            return true;
        }

        consoleUI.WriteError(
            string.IsNullOrWhiteSpace(detail)
                ? "Claude Code MCP registration failed. Run: claude mcp add fuse --scope user -- fuse mcp serve"
                : $"Claude Code MCP registration failed: {detail}");
        return false;
    }

    private static ClaudeMcpConfig LoadOrCreateClaude(string path)
    {
        if (!File.Exists(path))
            return new ClaudeMcpConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, FuseCliJsonContext.Default.ClaudeMcpConfig) ?? new ClaudeMcpConfig();
    }

    private static CursorMcpConfig LoadOrCreateCursor(string path)
    {
        if (!File.Exists(path))
            return new CursorMcpConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, FuseCliJsonContext.Default.CursorMcpConfig) ?? new CursorMcpConfig();
    }

    private static CopilotMcpConfig LoadOrCreateCopilot(string path)
    {
        if (!File.Exists(path))
            return new CopilotMcpConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize(json, FuseCliJsonContext.Default.CopilotMcpConfig) ?? new CopilotMcpConfig();
    }

    private static void WriteClaudeConfig(string path, ClaudeMcpConfig config) =>
        WriteJson(path, JsonSerializer.Serialize(config, FuseCliJsonContext.Default.ClaudeMcpConfig));

    private static void WriteCursorConfigFile(string path, CursorMcpConfig config) =>
        WriteJson(path, JsonSerializer.Serialize(config, FuseCliJsonContext.Default.CursorMcpConfig));

    private static void WriteCopilotConfigFile(string path, CopilotMcpConfig config) =>
        WriteJson(path, JsonSerializer.Serialize(config, FuseCliJsonContext.Default.CopilotMcpConfig));

    private static void WriteJson(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json + Environment.NewLine);
    }

    private static ClaudeMcpServer CreateClaudeServer(string fuseCommand) =>
        new()
        {
            Type = "stdio",
            Command = fuseCommand,
            Args = [.. ServeArguments],
        };

    private static CursorMcpServer CreateCursorServer(string fuseCommand) =>
        new()
        {
            Command = fuseCommand,
            Args = [.. ServeArguments],
        };

    private static CopilotMcpServer CreateCopilotServer(string fuseCommand) =>
        new()
        {
            Type = "stdio",
            Command = fuseCommand,
            Args = [.. ServeArguments],
        };

    /// <summary>
    ///     Resolves the MCP config file path for a client and scope.
    /// </summary>
    /// <param name="client">The MCP client.</param>
    /// <param name="scope">Project or user scope.</param>
    /// <param name="projectRoot">The project root for project scope.</param>
    /// <returns>The config file path the installer writes.</returns>
    internal static string GetConfigPath(McpInstallClient client, McpInstallScope scope, string projectRoot)
    {
        if (scope == McpInstallScope.User)
        {
            var userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return client switch
            {
                // Claude Code stores user-scope servers in ~/.claude.json, which is owned by the Claude CLI; user
                // scope is registered via `claude mcp add --scope user`, not by writing a file. See RegisterClaudeUserAsync.
                McpInstallClient.Claude => throw new NotSupportedException(
                    "Claude Code user scope is registered through the Claude CLI, not a config file path."),
                McpInstallClient.Cursor => Path.Combine(userRoot, ".cursor", "mcp.json"),
                // VS Code reads user-level MCP config from the profile directory, not ~/.vscode.
                McpInstallClient.Copilot => Path.Combine(GetVsCodeUserConfigDirectory(), "mcp.json"),
                _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
            };
        }

        return client switch
        {
            McpInstallClient.Claude => Path.Combine(projectRoot, ".mcp.json"),
            McpInstallClient.Cursor => Path.Combine(projectRoot, ".cursor", "mcp.json"),
            McpInstallClient.Copilot => Path.Combine(projectRoot, ".vscode", "mcp.json"),
            _ => throw new ArgumentOutOfRangeException(nameof(client), client, null),
        };
    }

    private static string DescribeScope(McpInstallScope scope) =>
        scope == McpInstallScope.User ? "user scope, all projects" : "project";

    // Idempotency markers for the managed rule block in freeform instruction files (CLAUDE.md, copilot-
    // instructions.md). A re-run replaces the region between them; a future remove can excise it cleanly.
    private const string RuleBeginMarker = "<!-- fuse:begin (managed by `fuse mcp install --rules`; edit outside these markers) -->";
    private const string RuleEndMarker = "<!-- fuse:end -->";

    // Deliberately conservative: Fuse for surveying and context-gathering, grep for exact lookups. A blanket
    // "always prefer Fuse" would be wrong in non-.NET repos where the regex tier is weaker than native search.
    private static readonly string RuleBody = string.Join(
        "\n",
        "## Fuse: codebase context",
        "",
        "This repo has the Fuse MCP server. For gathering codebase context and verifying edits, prefer the `fuse_*` tools over reading files one by one or grepping broadly:",
        "",
        "- Start with `fuse_workspace` (action=map) to survey structure, symbols, routes, and counts.",
        "- Use `fuse_find` with kind=task to find where a feature lives, or kind=service|request|route|config to resolve wiring; then `fuse_context` to read the selected seeds.",
        "- Use `fuse_review` to scope a pull request or diff review.",
        "- The verified-edit loop: after an edit run `fuse_check`; before a signature change run `fuse_impact`; before done run `fuse_review`.",
        "",
        "Use built-in grep and file reads for exact string or symbol lookups, where they are the better tool.");

    /// <summary>
    ///     Writes the Fuse usage rule into the given client's instruction file, scope permitting.
    /// </summary>
    /// <param name="client">The MCP client whose instruction file to update.</param>
    /// <param name="scope">Project scope writes repo files; user scope writes only Claude's global memory.</param>
    /// <param name="projectRoot">The project root for project-scoped rule files.</param>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <returns><see langword="true" /> when a rule file was written; <see langword="false" /> when skipped.</returns>
    private static bool WriteClientRule(
        McpInstallClient client,
        McpInstallScope scope,
        string projectRoot,
        IConsoleUI consoleUI)
    {
        switch (client)
        {
            case McpInstallClient.Claude:
                var claudePath = scope == McpInstallScope.User
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "CLAUDE.md")
                    : Path.Combine(projectRoot, "CLAUDE.md");
                UpsertMarkedBlock(claudePath, RuleBody);
                consoleUI.WriteSuccess($"Wrote Fuse rule for Claude Code: {claudePath}");
                return true;

            case McpInstallClient.Cursor:
                if (scope == McpInstallScope.User)
                {
                    consoleUI.WriteStep("Cursor has no user-global rules file; skipped the rule (use project scope for the Cursor rule).");
                    return false;
                }

                var cursorPath = Path.Combine(projectRoot, ".cursor", "rules", "fuse.mdc");
                WriteCursorRuleFile(cursorPath);
                consoleUI.WriteSuccess($"Wrote Fuse rule for Cursor: {cursorPath}");
                return true;

            case McpInstallClient.Copilot:
                if (scope == McpInstallScope.User)
                {
                    consoleUI.WriteStep("GitHub Copilot has no user-global instructions file; skipped the rule (use project scope for the Copilot rule).");
                    return false;
                }

                var copilotPath = Path.Combine(projectRoot, ".github", "copilot-instructions.md");
                UpsertMarkedBlock(copilotPath, RuleBody);
                consoleUI.WriteSuccess($"Wrote Fuse rule for GitHub Copilot: {copilotPath}");
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    ///     Inserts or replaces the marker-delimited Fuse rule block in a freeform markdown instruction file,
    ///     preserving all content outside the markers.
    /// </summary>
    /// <param name="path">The instruction file path.</param>
    /// <param name="body">The rule body to wrap between the begin and end markers.</param>
    private static void UpsertMarkedBlock(string path, string body)
    {
        var block = RuleBeginMarker + "\n" + body + "\n" + RuleEndMarker;

        string content;
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path);
            var begin = existing.IndexOf(RuleBeginMarker, StringComparison.Ordinal);
            var end = existing.IndexOf(RuleEndMarker, StringComparison.Ordinal);
            if (begin >= 0 && end > begin)
            {
                // Replace the existing managed region in place, leaving surrounding content untouched.
                content = existing[..begin] + block + existing[(end + RuleEndMarker.Length)..];
            }
            else
            {
                // Append after the user's content with a blank-line separator.
                var separator = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : "\n";
                content = existing + separator + "\n" + block + "\n";
            }
        }
        else
        {
            content = block + "\n";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    ///     Writes the Cursor rule as a dedicated, fully managed <c>.mdc</c> file with always-apply frontmatter.
    /// </summary>
    /// <param name="path">The <c>.cursor/rules/fuse.mdc</c> path.</param>
    private static void WriteCursorRuleFile(string path)
    {
        var content = string.Join(
            "\n",
            "---",
            "description: Prefer Fuse MCP tools for codebase context gathering",
            "alwaysApply: true",
            "---",
            "",
            RuleBody) + "\n";

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    ///     Resolves the VS Code user profile directory that holds the user-level <c>mcp.json</c>.
    /// </summary>
    /// <returns>The platform-specific <c>Code/User</c> directory.</returns>
    /// <remarks>
    ///     Windows uses <c>%APPDATA%\Code\User</c>, macOS uses <c>~/Library/Application Support/Code/User</c>, and
    ///     other platforms honour <c>XDG_CONFIG_HOME</c> (defaulting to <c>~/.config</c>) under <c>Code/User</c>.
    /// </remarks>
    private static string GetVsCodeUserConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "Code", "User");

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrWhiteSpace(xdgConfig) ? Path.Combine(home, ".config") : xdgConfig;
        return Path.Combine(configHome, "Code", "User");
    }

    private static string? FindExecutableOnPath(string name)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        var extensions = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              ?? [".EXE", ".CMD", ".BAT"]
            : [string.Empty];

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
