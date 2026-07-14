using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Fuse.Indexing;

/// <summary>
///     SQLite-backed implementation of <see cref="IWorkspaceIndexStore" />, stored at
///     <c>.fuse/fuse.db</c> in WAL mode.
/// </summary>
/// <remarks>
///     <see cref="InitializeAsync" /> is the write path: pragmas, <see cref="IndexSchemaMigrator" />
///     (rebuild when the on-disk version is below <see cref="WorkspaceIndexSchema.TargetVersion" />),
///     FTS probe, and <c>index_meta</c> stamps. <see cref="OpenForReadAsync" /> is the warm read path:
///     schema verify only, no meta write, so concurrent foreground reads do not contend with a
///     background upgrade writer. Connections are pooled via <see cref="WorkspaceIndexConnectionFactory" />;
///     the pool is cleared on dispose.
///     Implementation is split across internal ports (<see cref="IndexSchemaMigrator" />,
///     <see cref="FtsSearchEngine" />, <see cref="SymbolGraphStore" />, <see cref="SessionStore" />);
///     this type is the only public entry for host and MCP callers.
/// </remarks>
public sealed class WorkspaceIndexStore : IWorkspaceIndexStore
{
    private readonly WorkspaceIndexConnectionFactory _connectionFactory;
    private readonly IndexSchemaMigrator _schema;
    private readonly FtsSearchEngine _fts;
    private readonly SymbolGraphStore _graph;
    private readonly SessionStore _sessions;
    private readonly ILogger<WorkspaceIndexStore>? _logger;
    private int _schemaVersion;
    private bool _initialized;

    /// <summary>
    ///     Initializes a new instance of the <see cref="WorkspaceIndexStore" /> class.
    /// </summary>
    /// <param name="databasePath">The absolute path to the index database file.</param>
    /// <param name="logger">An optional logger for lifecycle diagnostics.</param>
    /// <param name="busyTimeoutMilliseconds">
    ///     The SQLite <c>busy_timeout</c> for this store's connections. Defaults to the write-path timeout; a
    ///     read-tool open passes a short value so a contended store surfaces <c>index_busy</c> quickly (R18/R20).
    /// </param>
    public WorkspaceIndexStore(
        string databasePath,
        ILogger<WorkspaceIndexStore>? logger = null,
        int busyTimeoutMilliseconds = WorkspaceIndexConnectionFactory.DefaultBusyTimeoutMilliseconds)
    {
        _connectionFactory = new WorkspaceIndexConnectionFactory(databasePath, busyTimeoutMilliseconds);
        _logger = logger;
        _schema = new IndexSchemaMigrator(_connectionFactory, logger);
        _fts = new FtsSearchEngine(_connectionFactory, logger);
        _graph = new SymbolGraphStore(_connectionFactory, _fts);
        _sessions = new SessionStore(_connectionFactory);
    }

    /// <summary>
    ///     The <c>index_meta</c> key under which the indexer stamps the Fuse build that wrote the index, so a
    ///     later run can detect an incompatible upgrade and rebuild.
    /// </summary>
    public const string FuseVersionMetaKey = "fuse_version";

    /// <summary>
    ///     The <c>index_meta</c> key under which <c>fuse index --from-capture</c> stamps the absolute path to the
    ///     bundle's portable compiler log (C2), so <c>fuse_check</c> can answer oracle-grade from the captured
    ///     compilation without building on a machine that cannot restore or build.
    /// </summary>
    public const string CaptureComplogPathMetaKey = "capture_complog_path";

    /// <summary>The absolute path to the index database file.</summary>
    public string DatabasePath => _connectionFactory.DatabasePath;

    /// <summary>
    ///     Whether full-text search is available. False when the runtime lacks FTS5; searches then
    ///     return no hits until a fallback index is built.
    /// </summary>
    public bool FullTextSearchAvailable => _fts.Available;

    /// <inheritdoc />
    public Task<WorkspaceIndexInitializeOutcome> InitializeAsync(CancellationToken cancellationToken) =>
        WorkspaceIndexRecovery.SerializeAsync(
            _connectionFactory.DatabasePath,
            () => InitializeSerializedAsync(cancellationToken));

    private async Task<WorkspaceIndexInitializeOutcome> InitializeSerializedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await InitializeCoreAsync(cancellationToken);
        }
        catch (SqliteException ex) when (WorkspaceIndexRecovery.IsCorrupt(ex))
        {
            _logger?.LogWarning(
                ex,
                "Corrupt index at {DatabasePath}; deleting and recreating.",
                _connectionFactory.DatabasePath);
            _connectionFactory.ClearPool();
            WorkspaceIndexRecovery.DeleteDatabaseFiles(_connectionFactory.DatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(_connectionFactory.DatabasePath)!);
            await InitializeCoreAsync(cancellationToken);
            return new WorkspaceIndexInitializeOutcome(true, "corrupt database recovered");
        }
    }

    private async Task<WorkspaceIndexInitializeOutcome> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_connectionFactory.DatabasePath)!);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await _schema.PrepareDatabaseAsync(connection, cancellationToken);

        var priorSchemaVersion = await IndexSchemaMigrator.ReadVersionAsync(connection, cancellationToken);
        _schemaVersion = await IndexSchemaMigrator.MigrateAsync(connection, cancellationToken);
        var schemaRebuilt = priorSchemaVersion > 0 && priorSchemaVersion < WorkspaceIndexSchema.TargetVersion;
        await IndexSchemaMigrator.EnsureTablesAsync(connection, cancellationToken);

        var storedVersion = await IndexSchemaMigrator.ReadMetaAsync(connection, FuseVersionMetaKey, cancellationToken);
        if (!FuseBuildInfo.IsCompatible(storedVersion))
        {
            _logger?.LogInformation(
                "Index at {DatabasePath} was built by Fuse {StoredVersion}; rebuilding for {CurrentVersion}.",
                _connectionFactory.DatabasePath,
                storedVersion,
                FuseBuildInfo.Current);
            await IndexSchemaMigrator.RebuildAsync(connection, cancellationToken);
            _schemaVersion = WorkspaceIndexSchema.TargetVersion;
            return new WorkspaceIndexInitializeOutcome(true, $"after upgrade to {FuseBuildInfo.Current}");
        }

        var ftsAvailable = await _fts.TryCreateAsync(connection, cancellationToken);
        await IndexSchemaMigrator.WriteMetaAsync(
            connection,
            IndexAvailability.FtsAvailableMetaKey,
            IndexAvailability.ToFtsMetaValue(ftsAvailable),
            cancellationToken);
        MarkInitialized(_schemaVersion, ftsAvailable);
        _logger?.LogDebug(
            "Workspace index initialized at {DatabasePath} (schema v{SchemaVersion}, fts={FtsAvailable}).",
            _connectionFactory.DatabasePath,
            _schemaVersion,
            ftsAvailable);

        if (schemaRebuilt)
            return new WorkspaceIndexInitializeOutcome(true, "schema migration");

        return WorkspaceIndexInitializeOutcome.Normal;
    }

    /// <inheritdoc />
    public async Task<WorkspaceIndexReadOpenStatus> OpenForReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_connectionFactory.DatabasePath))
            return WorkspaceIndexReadOpenStatus.DatabaseMissing;

        try
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
            await _schema.PrepareDatabaseAsync(connection, cancellationToken);

            var version = await IndexSchemaMigrator.ReadVersionAsync(connection, cancellationToken);
            if (version != WorkspaceIndexSchema.TargetVersion)
                return WorkspaceIndexReadOpenStatus.SchemaMismatch;

            var storedVersion = await IndexSchemaMigrator.ReadMetaAsync(connection, FuseVersionMetaKey, cancellationToken);
            if (!FuseBuildInfo.IsCompatible(storedVersion))
                return WorkspaceIndexReadOpenStatus.IncompatibleVersion;

            var ftsAvailable = IndexAvailability.ParseFtsMeta(
                await IndexSchemaMigrator.ReadMetaAsync(connection, IndexAvailability.FtsAvailableMetaKey, cancellationToken));
            MarkInitialized(version, ftsAvailable);
            _logger?.LogDebug(
                "Workspace index opened read-only at {DatabasePath} (schema v{SchemaVersion}, fts={FtsAvailable}).",
                _connectionFactory.DatabasePath,
                _schemaVersion,
                ftsAvailable);
            return WorkspaceIndexReadOpenStatus.Ready;
        }
        catch (SqliteException ex) when (WorkspaceIndexRecovery.IsCorrupt(ex))
        {
            return WorkspaceIndexReadOpenStatus.SchemaMismatch;
        }
    }

    private void MarkInitialized(int schemaVersion, bool ftsAvailable)
    {
        _schemaVersion = schemaVersion;
        _fts.MarkAvailable(ftsAvailable);
        _initialized = true;
    }

    /// <inheritdoc />
    public async Task<WorkspaceIndexState> GetStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var version = _initialized ? _schemaVersion : await IndexSchemaMigrator.ReadVersionAsync(connection, cancellationToken);
        var fileCount = await IndexSchemaMigrator.CountAsync(connection, "files", cancellationToken);
        var symbolCount = await IndexSchemaMigrator.CountAsync(connection, "symbols", cancellationToken);
        var status = fileCount == 0 ? WorkspaceIndexStatus.Cold : WorkspaceIndexStatus.Warm;
        var mode = await IndexSchemaMigrator.ReadMetaAsync(connection, "index_mode", cancellationToken);
        var ftsAvailable = _initialized
            ? _fts.Available
            : IndexAvailability.ParseFtsMeta(
                await IndexSchemaMigrator.ReadMetaAsync(connection, IndexAvailability.FtsAvailableMetaKey, cancellationToken));
        return new WorkspaceIndexState(version, status, fileCount, symbolCount, mode, ftsAvailable);
    }

    /// <inheritdoc />
    public async Task SetMetaAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await IndexSchemaMigrator.WriteMetaAsync(connection, key, value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        return await IndexSchemaMigrator.ReadMetaAsync(connection, key, cancellationToken);
    }

    /// <inheritdoc />
    public Task SaveCheckSessionBaselineAsync(
        string sessionId, string root, IReadOnlyList<CheckDiagnostic> baseline, CancellationToken cancellationToken) =>
        _sessions.SaveCheckSessionBaselineAsync(sessionId, root, baseline, cancellationToken);

    /// <inheritdoc />
    public Task<CheckSessionBaseline?> GetCheckSessionBaselineAsync(string sessionId, CancellationToken cancellationToken) =>
        _sessions.GetCheckSessionBaselineAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public Task SaveClaimLedgerAsync(string sessionId, string root, string claimsJson, CancellationToken cancellationToken) =>
        _sessions.SaveClaimLedgerAsync(sessionId, root, claimsJson, cancellationToken);

    /// <inheritdoc />
    public Task<ClaimLedgerRecord?> GetClaimLedgerAsync(string sessionId, CancellationToken cancellationToken) =>
        _sessions.GetClaimLedgerAsync(sessionId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(string root, CancellationToken cancellationToken) =>
        _sessions.ListSessionsAsync(root, cancellationToken);

    /// <inheritdoc />
    public Task UpsertFilesAsync(IReadOnlyList<IndexedFileRecord> files, CancellationToken cancellationToken) =>
        _graph.UpsertFilesAsync(files, cancellationToken);

    /// <inheritdoc />
    public Task UpsertProjectsAsync(IReadOnlyList<ProjectRecord> projects, CancellationToken cancellationToken) =>
        _graph.UpsertProjectsAsync(projects, cancellationToken);

    /// <inheritdoc />
    public Task UpsertNodesAsync(IReadOnlyList<NodeRecord> nodes, CancellationToken cancellationToken) =>
        _graph.UpsertNodesAsync(nodes, cancellationToken);

    /// <inheritdoc />
    public Task UpsertSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken cancellationToken) =>
        _graph.UpsertSymbolsAsync(symbols, cancellationToken);

    /// <inheritdoc />
    public Task UpsertChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken) =>
        _graph.UpsertChunksAsync(chunks, cancellationToken);

    /// <inheritdoc />
    public Task UpsertEdgesAsync(IReadOnlyList<SemanticEdgeRecord> edges, CancellationToken cancellationToken) =>
        _graph.UpsertEdgesAsync(edges, cancellationToken);

    /// <inheritdoc />
    public Task UpsertRoutesAsync(IReadOnlyList<RouteRecord> routes, CancellationToken cancellationToken) =>
        _graph.UpsertRoutesAsync(routes, cancellationToken);

    /// <inheritdoc />
    public Task UpsertDiRegistrationsAsync(IReadOnlyList<DiRegistrationRecord> registrations, CancellationToken cancellationToken) =>
        _graph.UpsertDiRegistrationsAsync(registrations, cancellationToken);

    /// <inheritdoc />
    public Task UpsertOptionsBindingsAsync(IReadOnlyList<OptionsBindingRecord> bindings, CancellationToken cancellationToken) =>
        _graph.UpsertOptionsBindingsAsync(bindings, cancellationToken);

    /// <inheritdoc />
    public Task DeleteFileDataAsync(string normalizedPath, CancellationToken cancellationToken) =>
        _graph.DeleteFileDataAsync(normalizedPath, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
        _fts.SearchAsync(query, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SymbolListItem>> ListSymbolsAsync(int limit, CancellationToken cancellationToken) =>
        _graph.ListSymbolsAsync(limit, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SymbolListItem>> FindSymbolsByNameAsync(string nameFragment, int limit, CancellationToken cancellationToken) =>
        _graph.FindSymbolsByNameAsync(nameFragment, limit, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SymbolSignature>> GetSignaturesByNamesAsync(
        IReadOnlyCollection<string> names, int limitPerName, CancellationToken cancellationToken) =>
        _graph.GetSignaturesByNamesAsync(names, limitPerName, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SymbolSignature>> GetMembersOfTypeAsync(
        string typeName, int limit, CancellationToken cancellationToken) =>
        _graph.GetMembersOfTypeAsync(typeName, limit, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<RouteListItem>> ListRoutesAsync(int limit, CancellationToken cancellationToken) =>
        _graph.ListRoutesAsync(limit, cancellationToken);

    /// <inheritdoc />
    public Task<NodeRecord?> GetNodeAsync(string nodeId, CancellationToken cancellationToken) =>
        _graph.GetNodeAsync(nodeId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<NodeRecord>> FindNodesByDisplayNameAsync(string displayName, CancellationToken cancellationToken) =>
        _graph.FindNodesByDisplayNameAsync(displayName, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<NodeRecord>> GetNodesByFileAsync(string normalizedPath, CancellationToken cancellationToken) =>
        _graph.GetNodesByFileAsync(normalizedPath, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SemanticEdgeRecord>> GetAllEdgesAsync(CancellationToken cancellationToken) =>
        _graph.GetAllEdgesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SemanticEdgeRecord>> GetOutgoingEdgesAsync(string nodeId, CancellationToken cancellationToken) =>
        _graph.GetOutgoingEdgesAsync(nodeId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SemanticEdgeRecord>> GetIncomingEdgesAsync(string nodeId, CancellationToken cancellationToken) =>
        _graph.GetIncomingEdgesAsync(nodeId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileListItem>> FindFilesByPathAsync(string fragment, int limit, CancellationToken cancellationToken) =>
        _graph.FindFilesByPathAsync(fragment, limit, cancellationToken);

    /// <inheritdoc />
    public Task<int> GetFileTokenEstimateAsync(string normalizedPath, CancellationToken cancellationToken) =>
        _graph.GetFileTokenEstimateAsync(normalizedPath, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, int>> GetFileTokenEstimatesAsync(CancellationToken cancellationToken) =>
        _graph.GetFileTokenEstimatesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(
        IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken) =>
        _graph.GetContentHashesAsync(normalizedPaths, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, string>> GetAllFileHashesAsync(CancellationToken cancellationToken) =>
        _graph.GetAllFileHashesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetFilesByLanguageAsync(string language, CancellationToken cancellationToken) =>
        _graph.GetFilesByLanguageAsync(language, cancellationToken);

    /// <inheritdoc />
    public Task<int> GetRouteCountAsync(CancellationToken cancellationToken) =>
        _graph.GetRouteCountAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<LanguageCount>> GetLanguageCountsAsync(CancellationToken cancellationToken) =>
        _graph.GetLanguageCountsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileDependencyEdge>> GetFileDependencyEdgesAsync(CancellationToken cancellationToken) =>
        _graph.GetFileDependencyEdgesAsync(cancellationToken);

    /// <inheritdoc />
    public Task UpsertCoChangesAsync(IReadOnlyList<CoChangeRecord> records, CancellationToken cancellationToken) =>
        _graph.UpsertCoChangesAsync(records, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CoChangeRecord>> GetCoChangesForAsync(
        IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken) =>
        _graph.GetCoChangesForAsync(normalizedPaths, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _connectionFactory.ClearPool();
        return ValueTask.CompletedTask;
    }
}
