namespace Fuse.Cli.Services;

/// <summary>
///     Registration scope for <c>fuse mcp install</c>.
/// </summary>
public enum McpInstallScope
{
    /// <summary>Configure the current project directory only.</summary>
    Project,

    /// <summary>Configure the user so every project can use Fuse.</summary>
    User,
}
