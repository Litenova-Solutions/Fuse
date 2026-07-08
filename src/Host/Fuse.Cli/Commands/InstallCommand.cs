using DotMake.CommandLine;
using Fuse.Cli.Services;

namespace Fuse.Cli.Commands;

/// <summary>
///     Registers Fuse as an MCP server with Claude Code, Cursor, or GitHub Copilot.
/// </summary>
[CliCommand(
    Name = "install",
    Description = "Register Fuse as an MCP server with Claude Code, Cursor, or GitHub Copilot.",
    Parent = typeof(McpCommand))]
public sealed class InstallCommand
{
    private readonly IConsoleUI _consoleUI;
    private readonly McpInstallService _mcpInstallService;

    /// <summary>
    ///     Initializes a new instance of the <see cref="InstallCommand" /> class for CLI option binding only.
    /// </summary>
    /// <remarks>
    ///     Used by DotMake.CommandLine to bind options; the console UI is <see langword="null" />, so this instance
    ///     must not run.
    /// </remarks>
    public InstallCommand() : this(null!, null!)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="InstallCommand" /> class.
    /// </summary>
    /// <param name="consoleUI">The console UI for status output.</param>
    /// <param name="mcpInstallService">The service that writes MCP client configuration.</param>
    public InstallCommand(IConsoleUI consoleUI, McpInstallService mcpInstallService)
    {
        _consoleUI = consoleUI;
        _mcpInstallService = mcpInstallService;
    }

    /// <summary>
    ///     Gets or sets the AI client to configure: <c>claude</c>, <c>cursor</c>, <c>copilot</c>, or <c>all</c>.
    /// </summary>
    [CliOption(Description = "AI client to configure: claude, cursor, copilot, or all (default: all).")]
    public string Client { get; set; } = "all";

    /// <summary>
    ///     Gets or sets the registration scope: <c>project</c> (this directory) or <c>user</c> (all projects).
    /// </summary>
    [CliOption(Description = "Registration scope: project (this directory) or user (all projects for this user).")]
    public string Scope { get; set; } = "project";

    /// <summary>
    ///     Gets or sets the Fuse executable the client should launch; defaults to the running binary or <c>fuse</c>.
    /// </summary>
    [CliOption(Required = false, Description = "Executable the client launches (default: the running fuse binary or fuse on PATH).")]
    public string? Command { get; set; }

    /// <summary>
    ///     When set, also writes a short rule biasing the agent toward the <c>fuse_*</c> tools into each client's
    ///     instruction file (Claude <c>CLAUDE.md</c>, Cursor <c>.cursor/rules/fuse.mdc</c>, Copilot
    ///     <c>.github/copilot-instructions.md</c>).
    /// </summary>
    [CliOption(Required = false, Description = "Also write a project rule that biases the agent toward the fuse_* tools (CLAUDE.md, .cursor/rules/fuse.mdc, .github/copilot-instructions.md). Recommended.")]
    public bool Rules { get; set; }

    /// <summary>
    ///     When set, also writes Fuse's ambient-verification hooks (S3) into the project's Claude Code
    ///     <c>.claude/settings.json</c>: a PostToolUse hook running <c>fuse check --delta --fast</c> after edits and
    ///     a Stop hook running <c>fuse gate</c>. Requires the explicit flag; the merge preserves other settings and
    ///     is idempotent.
    /// </summary>
    [CliOption(Name = "--with-hooks", Required = false, Description = "Also write Claude Code ambient-verification hooks (PostToolUse -> fuse check --delta, Stop -> fuse gate) into project .claude/settings.json.")]
    public bool WithHooks { get; set; }

    /// <summary>
    ///     Writes MCP configuration for the selected client(s) and scope.
    /// </summary>
    /// <param name="context">The CLI invocation context.</param>
    /// <returns>A task that completes when registration finishes.</returns>
    /// <remarks>
    ///     Project scope writes JSON config files in the current directory. User scope writes user-global config
    ///     files for Cursor and Copilot, and invokes the Claude Code CLI for Claude. The MCP client launches
    ///     <c>fuse mcp serve</c> as a child process; you do not run it manually.
    /// </remarks>
    public async Task RunAsync(CliContext context)
    {
        if (!TryParseClient(Client, out var clients))
        {
            _consoleUI.WriteError("Unknown client. Use claude, cursor, copilot, or all.");
            return;
        }

        if (!TryParseScope(Scope, out var scope))
        {
            _consoleUI.WriteError("Unknown scope. Use project or user.");
            return;
        }

        var configured = await _mcpInstallService.InstallAsync(
            clients,
            scope,
            projectDirectory: null,
            fuseCommand: Command,
            writeRules: Rules,
            _consoleUI,
            context.CancellationToken);

        if (configured == 0)
        {
            _consoleUI.WriteError("No MCP clients were configured.");
            return;
        }

        _consoleUI.WriteResult(
            $"Configured {configured} client{(configured == 1 ? string.Empty : "s")}. " +
            "Your AI client will launch fuse mcp serve automatically when MCP is enabled.");

        if (WithHooks)
            WriteClaudeHooks();

        if (!Rules)
            _consoleUI.WriteStep(
                "Tip: re-run with --rules to also bias your agent toward the fuse_* tools "
                + "(writes a short rule into your client's instructions file).");
    }

    // Writes (or idempotently updates) the project's .claude/settings.json with Fuse's ambient-verification hooks.
    // The command the hooks invoke is the explicit --command or the fuse global tool on PATH; the working tree is
    // touched only under the explicit --with-hooks flag.
    private void WriteClaudeHooks()
    {
        var projectRoot = Directory.GetCurrentDirectory();
        var settingsPath = System.IO.Path.Combine(projectRoot, ".claude", "settings.json");
        var existing = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        if (ClaudeHooksConfig.AlreadyInstalled(existing))
        {
            _consoleUI.WriteStep($"Ambient-verification hooks already present in {settingsPath}.");
            return;
        }

        var fuseCommand = string.IsNullOrWhiteSpace(Command) ? "fuse" : Command!;
        var merged = ClaudeHooksConfig.Merge(existing, fuseCommand);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, merged);
        _consoleUI.WriteSuccess($"Wrote ambient-verification hooks to {settingsPath} (PostToolUse -> check --delta, Stop -> gate).");
        _consoleUI.WriteStep("Remove them by deleting the two fuse hook entries from that file's \"hooks\" section.");
    }

    private static bool TryParseClient(string value, out IReadOnlyList<McpInstallClient> clients)
    {
        clients = value.Trim().ToLowerInvariant() switch
        {
            "claude" => [McpInstallClient.Claude],
            "cursor" => [McpInstallClient.Cursor],
            "copilot" => [McpInstallClient.Copilot],
            "all" => [McpInstallClient.Claude, McpInstallClient.Cursor, McpInstallClient.Copilot],
            _ => [],
        };

        return clients.Count > 0;
    }

    private static bool TryParseScope(string value, out McpInstallScope scope)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "project":
                scope = McpInstallScope.Project;
                return true;
            case "user":
                scope = McpInstallScope.User;
                return true;
            default:
                scope = default;
                return false;
        }
    }
}
