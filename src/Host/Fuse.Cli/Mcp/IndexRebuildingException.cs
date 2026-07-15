namespace Fuse.Cli.Mcp;

/// <summary>
///     Raised when the index store was rebuilt empty and must be re-indexed from source before the current
///     read can proceed (R21). MCP tools map this to the stable <c>index_rebuilding:</c> prefix.
/// </summary>
public sealed class IndexRebuildingException : Exception
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="IndexRebuildingException" /> class.
    /// </summary>
    /// <param name="detail">The human-readable rebuild reason (for example an upgrade version).</param>
    public IndexRebuildingException(string detail)
        : base(detail)
    {
    }
}
