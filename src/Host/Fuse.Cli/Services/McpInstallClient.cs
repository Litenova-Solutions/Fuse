namespace Fuse.Cli.Services;

/// <summary>
///     Supported MCP clients for <c>fuse mcp install</c>.
/// </summary>
public enum McpInstallClient
{
    /// <summary>Claude Code.</summary>
    Claude,

    /// <summary>Cursor.</summary>
    Cursor,

    /// <summary>GitHub Copilot in VS Code.</summary>
    Copilot,
}
