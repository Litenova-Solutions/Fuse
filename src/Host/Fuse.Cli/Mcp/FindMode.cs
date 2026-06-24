namespace Fuse.Cli.Mcp;

/// <summary>
///     Selects which kind of exact lookup the <c>fuse_find</c> MCP tool performs.
/// </summary>
public enum FindMode
{
    /// <summary>
    ///     Locate a declared symbol (a type or a member) by its exact simple name, returning the files and
    ///     declarations that define it.
    /// </summary>
    Symbol,

    /// <summary>
    ///     Locate an exact substring in file content, returning each match with surrounding context lines.
    /// </summary>
    Text,

    /// <summary>
    ///     Locate files whose path contains the query as a substring.
    /// </summary>
    Path
}
