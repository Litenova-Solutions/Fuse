namespace Fuse.Cli.Mcp;

/// <summary>
///     Helpers for the ambient availability header and related refusal messages on store-backed reads.
/// </summary>
internal static class AvailabilityHeaderHelpers
{
    /// <summary>
    ///     Renders the FTS availability clause embedded in the ambient availability header.
    /// </summary>
    /// <param name="ftsAvailable">Whether FTS5 full-text search initialized successfully.</param>
    /// <returns>A short clause naming FTS availability.</returns>
    internal static string FormatFtsAvailabilityClause(bool ftsAvailable) =>
        ftsAvailable ? "full-text search available" : "full-text search unavailable";

    /// <summary>
    ///     Renders the refusal for <c>fuse_find</c> (kind=task) when FTS5 is missing, prepending the header.
    /// </summary>
    /// <param name="availabilityHeader">The ambient availability header for the workspace.</param>
    /// <returns>An actionable refusal instead of empty task-localization hits.</returns>
    internal static string FormatTaskLocalizationFtsRefusal(string availabilityHeader) =>
        availabilityHeader + Environment.NewLine +
        "Refused: full-text search (FTS5) is unavailable in this runtime; fuse_find kind=task needs the lexical index to rank candidates. " +
        "Use a precise anchor (kind=symbol, path, service, request, route, or config) instead, or run the default Fuse global tool that bundles FTS5. " +
        "Check fuse_workspace action=status for availability.";
}
