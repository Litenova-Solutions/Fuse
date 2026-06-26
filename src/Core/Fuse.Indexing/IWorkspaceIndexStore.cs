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

    /// <summary>
    ///     Runs a full-text search over indexed chunks, ranked by relevance.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="cancellationToken">A token to cancel the search.</param>
    /// <returns>
    ///     Ranked hits (highest score first). Empty when the query is blank or full-text search is
    ///     unavailable in the current runtime.
    /// </returns>
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);

    /// <summary>
    ///     Lists indexed symbols, public-API first then by name, for summaries such as the workspace map.
    /// </summary>
    /// <param name="limit">The maximum number of symbols to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The symbol summaries.</returns>
    Task<IReadOnlyList<SymbolListItem>> ListSymbolsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    ///     Lists indexed routes ordered by pattern then method.
    /// </summary>
    /// <param name="limit">The maximum number of routes to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The route summaries.</returns>
    Task<IReadOnlyList<RouteListItem>> ListRoutesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Sets a key in the index metadata table.</summary>
    /// <param name="key">The metadata key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="cancellationToken">A token to cancel the write.</param>
    /// <returns>A task that completes when the value is committed.</returns>
    Task SetMetaAsync(string key, string value, CancellationToken cancellationToken);

    /// <summary>Reads a key from the index metadata table.</summary>
    /// <param name="key">The metadata key.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The stored value, or null when the key is absent.</returns>
    Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken);

    /// <summary>Reads a single node by id.</summary>
    /// <param name="nodeId">The node id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The node, or null when absent. <see cref="NodeRecord.FilePath" /> is the file's normalized path.</returns>
    Task<NodeRecord?> GetNodeAsync(string nodeId, CancellationToken cancellationToken);

    /// <summary>Finds nodes whose display name matches exactly (case-insensitive).</summary>
    /// <param name="displayName">The display name to match.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching nodes.</returns>
    Task<IReadOnlyList<NodeRecord>> FindNodesByDisplayNameAsync(string displayName, CancellationToken cancellationToken);

    /// <summary>Returns the edges leaving a node.</summary>
    /// <param name="nodeId">The source node id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The outgoing edges. <see cref="SemanticEdgeRecord.EvidenceFilePath" /> is the evidence file's normalized path or null.</returns>
    Task<IReadOnlyList<SemanticEdgeRecord>> GetOutgoingEdgesAsync(string nodeId, CancellationToken cancellationToken);

    /// <summary>Returns the edges entering a node.</summary>
    /// <param name="nodeId">The target node id.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The incoming edges.</returns>
    Task<IReadOnlyList<SemanticEdgeRecord>> GetIncomingEdgesAsync(string nodeId, CancellationToken cancellationToken);

    /// <summary>Finds indexed files whose normalized path contains a fragment (case-insensitive).</summary>
    /// <param name="fragment">The path fragment to match.</param>
    /// <param name="limit">The maximum number of files to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matching files.</returns>
    Task<IReadOnlyList<FileListItem>> FindFilesByPathAsync(string fragment, int limit, CancellationToken cancellationToken);
}
