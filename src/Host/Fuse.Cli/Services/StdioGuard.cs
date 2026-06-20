namespace Fuse.Cli.Services;

/// <summary>
///     Detects stdio redirection used by MCP and other pipe-based integrations.
/// </summary>
internal static class StdioGuard
{
    /// <summary>
    ///     Returns <c>true</c> when stdin or stdout are redirected.
    /// </summary>
    public static bool IsStdioRedirected() =>
        Console.IsInputRedirected || Console.IsOutputRedirected;
}
