using Fuse.Cli.Services;
using Fuse.Fusion;
using Fuse.Fusion.Scoping;
using Fuse.Indexing;
using Microsoft.Data.Sqlite;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Shared operational error taxonomy (R15): maps expected failures to stable prefixes for MCP tools and CLI
///     commands that open the index store, so clients never see an unhandled exception or a stack trace.
/// </summary>
internal static class FuseOperationalErrors
{
    /// <summary>Prefix when the index database is locked or busy (SQLite contention).</summary>
    public const string IndexBusyPrefix = "index_busy:";

    /// <summary>Prefix when no completed index exists.</summary>
    public const string IndexNotBuiltPrefix = "index_not_built:";

    /// <summary>Prefix when the workspace directory does not exist.</summary>
    public const string WorkspaceNotFoundPrefix = "workspace_not_found:";

    /// <summary>Prefix when a workspace-scoped MCP tool cannot resolve a Git repository identity.</summary>
    public const string WorkspaceIdentityUnresolvedPrefix = "workspace_identity_unresolved:";

    /// <summary>Prefix for caller input or validation failures.</summary>
    public const string ValidationErrorPrefix = "validation_error:";

    /// <summary>Prefix when the index was rebuilt empty and is being re-indexed from source.</summary>
    public const string IndexRebuildingPrefix = "index_rebuilding:";

    /// <summary>Prefix for unexpected operational failures.</summary>
    public const string InternalErrorPrefix = "internal_error:";

    /// <summary>
    ///     Runs an MCP tool body and converts any thrown exception into a stable prefixed string (never rethrows).
    /// </summary>
    /// <param name="action">The tool implementation.</param>
    /// <returns>The tool result or a prefixed operational error.</returns>
    internal static async Task<string> ExecuteMcpAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            return FromException(ex);
        }
    }

    /// <summary>
    ///     Maps an exception to a single-line prefixed message suitable for MCP or CLI stderr.
    /// </summary>
    /// <param name="exception">The caught exception.</param>
    /// <returns>A stable prefixed error string with no stack trace.</returns>
    internal static string FromException(Exception exception)
    {
        switch (exception)
        {
            case OperationCanceledException:
                return Format(InternalErrorPrefix, "operation canceled.");
            case IndexRebuildingException ex:
                FuseMetrics.RecordDegraded(DegradedStateKind.IndexRebuilding); // R37
                return Format(IndexRebuildingPrefix, ex.Message);
            case SearchIndexUnavailableException ex:
                FuseMetrics.RecordDegraded(DegradedStateKind.IndexRebuilding); // R37
                return Format(IndexRebuildingPrefix, ex.Message);
            case IndexBusyException ex:
                FuseMetrics.RecordDegraded(DegradedStateKind.IndexBusy); // R37
                return Format(IndexBusyPrefix, ex.Message);
            case WorkspaceIdentityException ex:
                return Format(WorkspaceIdentityUnresolvedPrefix, ex.Message);
            case DirectoryNotFoundException ex:
                return Format(WorkspaceNotFoundPrefix, $"directory not found: {ex.Message}");
            case FusionValidationException ex:
                return Format(ValidationErrorPrefix, string.Join("; ", ex.Errors));
            case ArgumentException ex:
                return Format(ValidationErrorPrefix, ex.Message);
            // A missing git precondition (not a repository, no such base ref) is an expected operational condition
            // for change-scoped tools (fuse_review), not an internal failure; surface it as validation_error so the
            // caller sees an actionable message rather than an opaque internal_error prefix.
            case Fuse.Retrieval.ChangeSourceException ex:
                return Format(ValidationErrorPrefix, ex.Message);
            case ChangeDetectionException ex:
                return Format(ValidationErrorPrefix, ex.Message);
            case SqliteException ex when IsSqliteBusyOrLocked(ex):
                FuseMetrics.RecordDegraded(DegradedStateKind.IndexBusy); // R37
                return Format(IndexBusyPrefix, "the index database is locked or busy; retry shortly or use a shared fuse host.");
            case SqliteException ex when IsMissingSearchTable(ex):
                // R23: a search issued against a store missing chunk_fts must never surface as a raw internal error;
                // it is a rebuildable derived-data gap, so it maps to index_rebuilding: and the read path rebuilds.
                FuseMetrics.RecordDegraded(DegradedStateKind.IndexRebuilding); // R37
                return Format(IndexRebuildingPrefix, "the full-text search index is missing; the index is rebuilding.");
            case SqliteException ex:
                return Format(InternalErrorPrefix, ex.Message);
            case IOException ex when IsSharingViolation(ex):
                FuseMetrics.RecordDegraded(DegradedStateKind.IndexBusy); // R37
                return Format(IndexBusyPrefix, "the index database is in use by another process; retry shortly or use a shared fuse host.");
            case IOException ex:
                return Format(InternalErrorPrefix, ex.Message);
            default:
                return Format(InternalErrorPrefix, exception.Message);
        }
    }

    /// <summary>Formats a prefixed operational error.</summary>
    /// <param name="prefix">One of the stable prefixes (including the trailing colon).</param>
    /// <param name="message">The human-readable detail.</param>
    /// <returns>A single-line prefixed message.</returns>
    internal static string Format(string prefix, string message) => $"{prefix} {message}";

    /// <summary>Formats a workspace-not-found error.</summary>
    /// <param name="root">The path that was not found.</param>
    internal static string FormatWorkspaceNotFound(string root) =>
        Format(WorkspaceNotFoundPrefix, $"directory not found: {root}");

    /// <summary>Formats an index-not-built error.</summary>
    /// <param name="databasePath">The expected database path.</param>
    internal static string FormatIndexNotBuilt(string databasePath) =>
        Format(IndexNotBuiltPrefix, $"no index at {databasePath}. Run 'fuse index' or fuse_workspace action=index first.");

    /// <summary>
    ///     Writes a prefixed operational error to stderr and sets exit code 1 for CLI commands.
    /// </summary>
    /// <param name="console">The console UI.</param>
    /// <param name="prefixedMessage">A message that already includes a stable prefix.</param>
    internal static void WriteCliError(IConsoleUI console, string prefixedMessage)
    {
        console.WriteError(prefixedMessage);
        Environment.ExitCode = 1;
    }

    /// <summary>
    ///     Maps an exception to stderr (one line, no stack trace) and sets exit code 1.
    /// </summary>
    /// <param name="console">The console UI.</param>
    /// <param name="exception">The caught exception.</param>
    internal static void ReportCli(IConsoleUI console, Exception exception) =>
        WriteCliError(console, FromException(exception));

    private static bool IsSqliteBusyOrLocked(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6; // SQLITE_BUSY, SQLITE_LOCKED

    private static bool IsMissingSearchTable(SqliteException exception) =>
        exception.SqliteErrorCode == 1 // SQLITE_ERROR
        && exception.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);

    private static bool IsSharingViolation(IOException exception) =>
        exception.HResult is unchecked((int)0x80070020); // ERROR_SHARING_VIOLATION
}
