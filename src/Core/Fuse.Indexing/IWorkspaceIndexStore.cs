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

    /// <summary>Inserts or updates files, preserving the integer <c>file_id</c> across re-index.</summary>
    /// <param name="files">The files to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    /// <remarks>The whole batch is committed in a single transaction.</remarks>
    Task UpsertFilesAsync(IReadOnlyList<IndexedFileRecord> files, CancellationToken cancellationToken);

    /// <summary>Inserts or updates projects.</summary>
    /// <param name="projects">The projects to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertProjectsAsync(IReadOnlyList<ProjectRecord> projects, CancellationToken cancellationToken);

    /// <summary>Inserts or updates semantic graph nodes.</summary>
    /// <param name="nodes">The nodes to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    /// <remarks>Nodes must exist before the edges that reference them are upserted.</remarks>
    Task UpsertNodesAsync(IReadOnlyList<NodeRecord> nodes, CancellationToken cancellationToken);

    /// <summary>Inserts or updates symbols.</summary>
    /// <param name="symbols">The symbols to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    /// <remarks>Symbols whose declaring file is not yet indexed are skipped.</remarks>
    Task UpsertSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken cancellationToken);

    /// <summary>Inserts or updates chunks.</summary>
    /// <param name="chunks">The chunks to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    /// <remarks>Chunks whose owning file is not yet indexed are skipped.</remarks>
    Task UpsertChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken);

    /// <summary>Inserts or updates semantic edges.</summary>
    /// <param name="edges">The edges to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    /// <remarks>Both endpoint nodes must already exist; the edge identity is derived from its endpoints, type, and evidence file.</remarks>
    Task UpsertEdgesAsync(IReadOnlyList<SemanticEdgeRecord> edges, CancellationToken cancellationToken);

    /// <summary>Inserts or updates routes.</summary>
    /// <param name="routes">The routes to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertRoutesAsync(IReadOnlyList<RouteRecord> routes, CancellationToken cancellationToken);

    /// <summary>Inserts or updates dependency-injection registrations.</summary>
    /// <param name="registrations">The registrations to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertDiRegistrationsAsync(IReadOnlyList<DiRegistrationRecord> registrations, CancellationToken cancellationToken);

    /// <summary>Inserts or updates options/config bindings.</summary>
    /// <param name="bindings">The bindings to upsert.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the batch is committed.</returns>
    Task UpsertOptionsBindingsAsync(IReadOnlyList<OptionsBindingRecord> bindings, CancellationToken cancellationToken);

    /// <summary>
    ///     Deletes all per-file derived data (symbols, chunks, nodes, edges, routes, DI registrations,
    ///     options bindings) for a file, leaving the <c>files</c> row in place for re-upsert.
    /// </summary>
    /// <param name="normalizedPath">The normalized path of the file whose data to clear.</param>
    /// <param name="cancellationToken">A token to cancel the delete.</param>
    /// <returns>A task that completes when the delete is committed.</returns>
    /// <remarks>Used by incremental re-index: clear a changed file's data, then re-upsert it.</remarks>
    Task DeleteFileDataAsync(string normalizedPath, CancellationToken cancellationToken);
}
