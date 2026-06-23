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
    [CliOption(Description = "Executable the client launches (default: the running fuse binary or fuse on PATH).")]
    public string? Command { get; set; }

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
