namespace Fuse.Indexing;

/// <summary>
///     The outcome of a read-only warm open (<see cref="IWorkspaceIndexStore.OpenForReadAsync" />).
/// </summary>
public enum WorkspaceIndexReadOpenStatus
{
    /// <summary>The database exists, schema matches, and the store is ready for reads.</summary>
    Ready,

    /// <summary>The database file does not exist yet; call <see cref="IWorkspaceIndexStore.InitializeAsync" />.</summary>
    DatabaseMissing,

    /// <summary>The on-disk schema version is below the target; write initialization is required.</summary>
    SchemaMismatch,

    /// <summary>
    ///     The index was stamped by an incompatible Fuse build; write initialization must rebuild it.
    /// </summary>
    IncompatibleVersion,
}

/// <summary>
///     The indexing status of a workspace, reported by the warm host and the CLI.
/// </summary>
public enum WorkspaceIndexStatus
{
    /// <summary>No index data exists yet.</summary>
    Cold,

    /// <summary>An indexing pass is in progress.</summary>
    Indexing,

    /// <summary>The index is current and fully semantic.</summary>
    Warm,

    /// <summary>The index exists but some projects fell back to syntax-only analysis.</summary>
    Partial,

    /// <summary>Indexing failed.</summary>
    Failed,

    /// <summary>The index exists but is out of date relative to the working tree.</summary>
    Stale,
}

/// <summary>
///     A point-in-time summary of the workspace index: its schema version, status, and record counts.
/// </summary>
/// <param name="SchemaVersion">The schema version currently on disk.</param>
/// <param name="Status">The indexing status.</param>
/// <param name="FileCount">The number of indexed files.</param>
/// <param name="SymbolCount">The number of indexed symbols.</param>
/// <param name="Mode">The last index mode (<c>semantic</c>, <c>partial</c>, or <c>syntax</c>), or null if never indexed.</param>
/// <param name="FtsAvailable">Whether FTS5 full-text search was available when the store last initialized.</param>
public sealed record WorkspaceIndexState(
    int SchemaVersion,
    WorkspaceIndexStatus Status,
    int FileCount,
    int SymbolCount,
    string? Mode = null,
    bool FtsAvailable = true);
