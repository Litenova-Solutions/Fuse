namespace Fuse.Cli.Mcp;

/// <summary>
///     Thrown when the per-root index writer lock cannot be acquired (cross-process contention). Mapped to
///     <see cref="FuseOperationalErrors.IndexBusyPrefix" /> at MCP and CLI boundaries.
/// </summary>
public sealed class IndexBusyException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="IndexBusyException" /> class.</summary>
    public IndexBusyException()
        : base("the index database is locked or busy; retry shortly or use a shared fuse host.")
    {
    }
}
