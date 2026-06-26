namespace Fuse.Indexing;

/// <summary>
///     The persistent SQLite store backing the workspace semantic index.
/// </summary>
/// <remarks>
///     This supersedes the earlier key-value and relevance-postings caches as the canonical store.
///     The upsert, delete, search, and edge-traversal members are added in later overhaul phases; this
///     phase establishes the database lifecycle (open, migrate, report state, dispose).
/// </remarks>
public interface IWorkspaceIndexStore : IAsyncDisposable
{
    /// <summary>
    ///     Opens the database, brings the schema to the current version (rebuilding from scratch when
    ///     the on-disk version is older), and applies the database-level pragmas.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel initialization.</param>
    /// <returns>A task that completes when the store is ready for use.</returns>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Returns the current schema version, status, and record counts.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>A <see cref="WorkspaceIndexState" /> snapshot.</returns>
    Task<WorkspaceIndexState> GetStateAsync(CancellationToken cancellationToken);
}
