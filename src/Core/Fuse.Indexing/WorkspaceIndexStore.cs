using System.IO.Hashing;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Fuse.Indexing;

/// <summary>
///     SQLite-backed implementation of <see cref="IWorkspaceIndexStore" />, stored at
///     <c>.fuse/fuse.db</c> in WAL mode.
/// </summary>
/// <remarks>
///     On <see cref="InitializeAsync" /> the store applies the database-level pragmas and runs
///     <see cref="WorkspaceIndexMigrator" />, which rebuilds the schema from scratch when the on-disk
///     version is below <see cref="WorkspaceIndexSchema.TargetVersion" />. Connections are pooled via
///     <see cref="WorkspaceIndexConnectionFactory" />; the pool is cleared on dispose.
/// </remarks>
public sealed class WorkspaceIndexStore : IWorkspaceIndexStore
{
    private readonly WorkspaceIndexConnectionFactory _connectionFactory;
    private readonly ILogger<WorkspaceIndexStore>? _logger;
    private int _schemaVersion;
    private bool _initialized;
    private bool _ftsAvailable;

    /// <summary>
    ///     Initializes a new instance of the <see cref="WorkspaceIndexStore" /> class.
    /// </summary>
    /// <param name="databasePath">The absolute path to the index database file.</param>
    /// <param name="logger">An optional logger for lifecycle diagnostics.</param>
    public WorkspaceIndexStore(string databasePath, ILogger<WorkspaceIndexStore>? logger = null)
    {
        _connectionFactory = new WorkspaceIndexConnectionFactory(databasePath);
        _logger = logger;
    }

    /// <summary>
    ///     The <c>index_meta</c> key under which the indexer stamps the Fuse build that wrote the index, so a
    ///     later run can detect an incompatible upgrade and rebuild.
    /// </summary>
    public const string FuseVersionMetaKey = "fuse_version";

    /// <summary>The absolute path to the index database file.</summary>
    public string DatabasePath => _connectionFactory.DatabasePath;

    /// <summary>
    ///     Whether full-text search is available. False when the runtime lacks FTS5; searches then
    ///     return no hits until a fallback index is built.
    /// </summary>
    public bool FullTextSearchAvailable => _ftsAvailable;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_connectionFactory.DatabasePath)!);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await ApplyDatabasePragmasAsync(connection, cancellationToken);
        _schemaVersion = await WorkspaceIndexMigrator.MigrateAsync(connection, cancellationToken);
        // Ensure the relational schema on every init (all statements are IF NOT EXISTS). This self-heals an
        // additive schema change made within the same schema version, where migration is skipped because the
        // on-disk version already equals the target.
        await EnsureTablesAsync(connection, cancellationToken);

        // Version-drift self-heal: if the index was written by a Fuse whose major.minor differs from this
        // build, its extraction contract may have changed, so drop and rebuild the schema. The store is then
        // empty and the next index pass repopulates it. Gated to major.minor so a patch upgrade does not force
        // a costly reindex, and skipped when no version was stamped (a pre-stamp index) so it is not wiped
        // blindly. This runs after EnsureTables so index_meta exists to read the stamp from.
        var storedVersion = await ReadMetaAsync(connection, FuseVersionMetaKey, cancellationToken);
        if (!FuseBuildInfo.IsCompatible(storedVersion))
        {
            _logger?.LogInformation(
                "Index at {DatabasePath} was built by Fuse {StoredVersion}; rebuilding for {CurrentVersion}.",
                _connectionFactory.DatabasePath,
                storedVersion,
                FuseBuildInfo.Current);
            await WorkspaceIndexMigrator.RebuildAsync(connection, cancellationToken);
            _schemaVersion = WorkspaceIndexSchema.TargetVersion;
        }

        _ftsAvailable = await TryCreateFtsAsync(connection, cancellationToken);
        _initialized = true;
        _logger?.LogDebug(
            "Workspace index initialized at {DatabasePath} (schema v{SchemaVersion}, fts={FtsAvailable}).",
            _connectionFactory.DatabasePath,
            _schemaVersion,
            _ftsAvailable);
    }

    private static async Task EnsureTablesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = WorkspaceIndexSchema.CreateTablesDdl;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // FTS5 ships with the bundled SQLite, but a stripped runtime can lack it. Creating the virtual table is the
    // honest availability probe: on failure the relational schema is intact and search degrades to empty rather
    // than crashing the run.
    private async Task<bool> TryCreateFtsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = WorkspaceIndexSchema.CreateFtsDdl;
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (SqliteException ex)
        {
            _logger?.LogWarning(ex, "FTS5 unavailable at {DatabasePath}; full-text search disabled.", _connectionFactory.DatabasePath);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<WorkspaceIndexState> GetStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var version = _initialized ? _schemaVersion : await ReadVersionAsync(connection, cancellationToken);
        var fileCount = await CountAsync(connection, "files", cancellationToken);
        var symbolCount = await CountAsync(connection, "symbols", cancellationToken);
        var status = fileCount == 0 ? WorkspaceIndexStatus.Cold : WorkspaceIndexStatus.Warm;
        var mode = await ReadMetaAsync(connection, "index_mode", cancellationToken);
        return new WorkspaceIndexState(version, status, fileCount, symbolCount, mode);
    }

    /// <inheritdoc />
    public async Task SetMetaAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO index_meta(key, value) VALUES($k, $v) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        return await ReadMetaAsync(connection, key, cancellationToken);
    }

    private static async Task<string?> ReadMetaAsync(SqliteConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM index_meta WHERE key = $k LIMIT 1;";
        command.Parameters.AddWithValue("$k", key);
        try
        {
            return await command.ExecuteScalarAsync(cancellationToken) as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task UpsertFilesAsync(IReadOnlyList<IndexedFileRecord> files, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var projectIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO files(path, normalized_path, extension, size_bytes, mtime_utc_ticks, content_hash,
                              project_id, is_generated, is_test, language, indexed_at_utc)
            VALUES($path, $norm, $ext, $size, $mtime, $hash, $project, $generated, $test, $language, $indexed)
            ON CONFLICT(normalized_path) DO UPDATE SET
              path = excluded.path, extension = excluded.extension, size_bytes = excluded.size_bytes,
              mtime_utc_ticks = excluded.mtime_utc_ticks, content_hash = excluded.content_hash,
              project_id = excluded.project_id, is_generated = excluded.is_generated,
              is_test = excluded.is_test, language = excluded.language, indexed_at_utc = excluded.indexed_at_utc;
            """;
        var pathParam = command.Parameters.Add("$path", SqliteType.Text);
        var normParam = command.Parameters.Add("$norm", SqliteType.Text);
        var extParam = command.Parameters.Add("$ext", SqliteType.Text);
        var sizeParam = command.Parameters.Add("$size", SqliteType.Integer);
        var mtimeParam = command.Parameters.Add("$mtime", SqliteType.Integer);
        var hashParam = command.Parameters.Add("$hash", SqliteType.Text);
        var projectParam = command.Parameters.Add("$project", SqliteType.Integer);
        var generatedParam = command.Parameters.Add("$generated", SqliteType.Integer);
        var testParam = command.Parameters.Add("$test", SqliteType.Integer);
        var languageParam = command.Parameters.Add("$language", SqliteType.Text);
        var indexedParam = command.Parameters.Add("$indexed", SqliteType.Text);

        foreach (var file in files)
        {
            pathParam.Value = file.Path;
            normParam.Value = file.NormalizedPath;
            extParam.Value = file.Extension;
            sizeParam.Value = file.SizeBytes;
            mtimeParam.Value = file.MtimeUtcTicks;
            hashParam.Value = file.ContentHash;
            projectParam.Value = (object?)await ResolveProjectIdAsync(connection, transaction, file.ProjectPath, projectIds, cancellationToken) ?? DBNull.Value;
            generatedParam.Value = file.IsGenerated ? 1 : 0;
            testParam.Value = file.IsTest ? 1 : 0;
            languageParam.Value = (object?)file.Language ?? DBNull.Value;
            indexedParam.Value = (file.IndexedAtUtc ?? DateTimeOffset.UtcNow).ToString("o");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertProjectsAsync(IReadOnlyList<ProjectRecord> projects, CancellationToken cancellationToken)
    {
        if (projects.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO projects(path, name, assembly_name, target_framework, project_hash, indexed_at_utc)
            VALUES($path, $name, $assembly, $tfm, $hash, $indexed)
            ON CONFLICT(path) DO UPDATE SET
              name = excluded.name, assembly_name = excluded.assembly_name,
              target_framework = excluded.target_framework, project_hash = excluded.project_hash,
              indexed_at_utc = excluded.indexed_at_utc;
            """;
        var pathParam = command.Parameters.Add("$path", SqliteType.Text);
        var nameParam = command.Parameters.Add("$name", SqliteType.Text);
        var assemblyParam = command.Parameters.Add("$assembly", SqliteType.Text);
        var tfmParam = command.Parameters.Add("$tfm", SqliteType.Text);
        var hashParam = command.Parameters.Add("$hash", SqliteType.Text);
        var indexedParam = command.Parameters.Add("$indexed", SqliteType.Text);

        foreach (var project in projects)
        {
            pathParam.Value = project.Path;
            nameParam.Value = project.Name;
            assemblyParam.Value = (object?)project.AssemblyName ?? DBNull.Value;
            tfmParam.Value = (object?)project.TargetFramework ?? DBNull.Value;
            hashParam.Value = project.ProjectHash;
            indexedParam.Value = (project.IndexedAtUtc ?? DateTimeOffset.UtcNow).ToString("o");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertNodesAsync(IReadOnlyList<NodeRecord> nodes, CancellationToken cancellationToken)
    {
        if (nodes.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);
        var projectIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO nodes(node_id, kind, display_name, file_id, project_id, symbol_id,
                                         stable_key, start_line, end_line, signature, metadata_json)
            VALUES($id, $kind, $display, $file, $project, $symbol, $stable, $start, $end, $sig, $meta);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var kindParam = command.Parameters.Add("$kind", SqliteType.Text);
        var displayParam = command.Parameters.Add("$display", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var projectParam = command.Parameters.Add("$project", SqliteType.Integer);
        var symbolParam = command.Parameters.Add("$symbol", SqliteType.Text);
        var stableParam = command.Parameters.Add("$stable", SqliteType.Text);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var sigParam = command.Parameters.Add("$sig", SqliteType.Text);
        var metaParam = command.Parameters.Add("$meta", SqliteType.Text);

        foreach (var node in nodes)
        {
            idParam.Value = node.NodeId;
            kindParam.Value = node.Kind;
            displayParam.Value = node.DisplayName;
            fileParam.Value = (object?)await ResolveFileIdAsync(connection, transaction, node.FilePath, fileIds, cancellationToken) ?? DBNull.Value;
            projectParam.Value = (object?)await ResolveProjectIdAsync(connection, transaction, node.ProjectPath, projectIds, cancellationToken) ?? DBNull.Value;
            symbolParam.Value = (object?)node.SymbolId ?? DBNull.Value;
            stableParam.Value = node.StableKey;
            startParam.Value = (object?)node.StartLine ?? DBNull.Value;
            endParam.Value = (object?)node.EndLine ?? DBNull.Value;
            sigParam.Value = (object?)node.Signature ?? DBNull.Value;
            metaParam.Value = (object?)node.MetadataJson ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertSymbolsAsync(IReadOnlyList<SymbolRecord> symbols, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);
        var projectIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO symbols(symbol_id, file_id, project_id, kind, name, fully_qualified_name,
                                           metadata_name, containing_type, namespace, assembly_name,
                                           accessibility, signature, start_line, end_line, is_public_api)
            VALUES($id, $file, $project, $kind, $name, $fqn, $meta, $containing, $ns, $assembly,
                   $access, $sig, $start, $end, $public);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var projectParam = command.Parameters.Add("$project", SqliteType.Integer);
        var kindParam = command.Parameters.Add("$kind", SqliteType.Text);
        var nameParam = command.Parameters.Add("$name", SqliteType.Text);
        var fqnParam = command.Parameters.Add("$fqn", SqliteType.Text);
        var metaParam = command.Parameters.Add("$meta", SqliteType.Text);
        var containingParam = command.Parameters.Add("$containing", SqliteType.Text);
        var nsParam = command.Parameters.Add("$ns", SqliteType.Text);
        var assemblyParam = command.Parameters.Add("$assembly", SqliteType.Text);
        var accessParam = command.Parameters.Add("$access", SqliteType.Text);
        var sigParam = command.Parameters.Add("$sig", SqliteType.Text);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var publicParam = command.Parameters.Add("$public", SqliteType.Integer);

        foreach (var symbol in symbols)
        {
            var fileId = await ResolveFileIdAsync(connection, transaction, symbol.FilePath, fileIds, cancellationToken);
            if (fileId is null)
                continue;

            idParam.Value = symbol.SymbolId;
            fileParam.Value = fileId.Value;
            projectParam.Value = (object?)await ResolveProjectIdAsync(connection, transaction, symbol.ProjectPath, projectIds, cancellationToken) ?? DBNull.Value;
            kindParam.Value = symbol.Kind;
            nameParam.Value = symbol.Name;
            fqnParam.Value = symbol.FullyQualifiedName;
            metaParam.Value = (object?)symbol.MetadataName ?? DBNull.Value;
            containingParam.Value = (object?)symbol.ContainingType ?? DBNull.Value;
            nsParam.Value = (object?)symbol.Namespace ?? DBNull.Value;
            assemblyParam.Value = (object?)symbol.AssemblyName ?? DBNull.Value;
            accessParam.Value = (object?)symbol.Accessibility ?? DBNull.Value;
            sigParam.Value = (object?)symbol.Signature ?? DBNull.Value;
            startParam.Value = symbol.StartLine;
            endParam.Value = symbol.EndLine;
            publicParam.Value = symbol.IsPublicApi ? 1 : 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertChunksAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO chunks(chunk_id, file_id, symbol_id, kind, name, stable_key,
                                          start_line, end_line, text_hash, token_estimate,
                                          reduced_token_estimate, signature, outline)
            VALUES($id, $file, $symbol, $kind, $name, $stable, $start, $end, $hash, $tokens,
                   $reduced, $sig, $outline);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var symbolParam = command.Parameters.Add("$symbol", SqliteType.Text);
        var kindParam = command.Parameters.Add("$kind", SqliteType.Text);
        var nameParam = command.Parameters.Add("$name", SqliteType.Text);
        var stableParam = command.Parameters.Add("$stable", SqliteType.Text);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var hashParam = command.Parameters.Add("$hash", SqliteType.Text);
        var tokensParam = command.Parameters.Add("$tokens", SqliteType.Integer);
        var reducedParam = command.Parameters.Add("$reduced", SqliteType.Integer);
        var sigParam = command.Parameters.Add("$sig", SqliteType.Text);
        var outlineParam = command.Parameters.Add("$outline", SqliteType.Text);

        // FTS is a standalone (non-content) table managed manually: delete then insert by chunk_id so a
        // re-indexed chunk replaces its prior search row instead of duplicating it.
        SqliteCommand? ftsDelete = null;
        SqliteCommand? ftsInsert = null;
        if (_ftsAvailable)
        {
            ftsDelete = connection.CreateCommand();
            ftsDelete.Transaction = transaction;
            ftsDelete.CommandText = "DELETE FROM chunk_fts WHERE chunk_id = $id;";
            ftsDelete.Parameters.Add("$id", SqliteType.Text);

            ftsInsert = connection.CreateCommand();
            ftsInsert.Transaction = transaction;
            ftsInsert.CommandText = """
                INSERT INTO chunk_fts(chunk_id, path, name, symbols, signature, comments, body, subtokens, stems)
                VALUES($id, $path, $name, $symbols, $sig, $comments, $body, $subtokens, $stems);
                """;
            ftsInsert.Parameters.Add("$id", SqliteType.Text);
            ftsInsert.Parameters.Add("$path", SqliteType.Text);
            ftsInsert.Parameters.Add("$name", SqliteType.Text);
            ftsInsert.Parameters.Add("$symbols", SqliteType.Text);
            ftsInsert.Parameters.Add("$sig", SqliteType.Text);
            ftsInsert.Parameters.Add("$comments", SqliteType.Text);
            ftsInsert.Parameters.Add("$body", SqliteType.Text);
            ftsInsert.Parameters.Add("$subtokens", SqliteType.Text);
            ftsInsert.Parameters.Add("$stems", SqliteType.Text);
        }

        try
        {
            foreach (var chunk in chunks)
            {
                var fileId = await ResolveFileIdAsync(connection, transaction, chunk.FilePath, fileIds, cancellationToken);
                if (fileId is null)
                    continue;

                idParam.Value = chunk.ChunkId;
                fileParam.Value = fileId.Value;
                symbolParam.Value = (object?)chunk.SymbolId ?? DBNull.Value;
                kindParam.Value = chunk.Kind;
                nameParam.Value = (object?)chunk.Name ?? DBNull.Value;
                stableParam.Value = chunk.StableKey;
                startParam.Value = chunk.StartLine;
                endParam.Value = chunk.EndLine;
                hashParam.Value = chunk.TextHash;
                tokensParam.Value = chunk.TokenEstimate;
                reducedParam.Value = chunk.ReducedTokenEstimate;
                sigParam.Value = (object?)chunk.Signature ?? DBNull.Value;
                outlineParam.Value = (object?)chunk.Outline ?? DBNull.Value;
                await command.ExecuteNonQueryAsync(cancellationToken);

                if (ftsDelete is not null && ftsInsert is not null)
                {
                    ftsDelete.Parameters["$id"].Value = chunk.ChunkId;
                    await ftsDelete.ExecuteNonQueryAsync(cancellationToken);

                    ftsInsert.Parameters["$id"].Value = chunk.ChunkId;
                    ftsInsert.Parameters["$path"].Value = chunk.FilePath;
                    ftsInsert.Parameters["$name"].Value = (object?)chunk.Name ?? DBNull.Value;
                    ftsInsert.Parameters["$symbols"].Value = (object?)chunk.SymbolsText ?? DBNull.Value;
                    ftsInsert.Parameters["$sig"].Value = (object?)chunk.Signature ?? DBNull.Value;
                    ftsInsert.Parameters["$comments"].Value = (object?)chunk.Comments ?? DBNull.Value;
                    ftsInsert.Parameters["$body"].Value = (object?)chunk.Body ?? DBNull.Value;
                    // The subtokens field is the subword expansion of the chunk's identifiers (name, declared
                    // symbols, signature), computed in C# so a prose query word matches a compound name.
                    var subtokens = IdentifierSplitter.Expand(chunk.Name, chunk.SymbolsText, chunk.Signature);
                    ftsInsert.Parameters["$subtokens"].Value = subtokens.Length == 0 ? DBNull.Value : subtokens;
                    // The stems field is the Porter-stemmed form of the chunk's identifiers and comment prose, so
                    // an inflected query word (rounds, rounded) matches an inflected code or comment word (rounding).
                    var stems = PorterStemmer.Expand(chunk.Name, chunk.SymbolsText, chunk.Signature, chunk.Comments);
                    ftsInsert.Parameters["$stems"].Value = stems.Length == 0 ? DBNull.Value : stems;
                    await ftsInsert.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
        finally
        {
            ftsDelete?.Dispose();
            ftsInsert?.Dispose();
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertEdgesAsync(IReadOnlyList<SemanticEdgeRecord> edges, CancellationToken cancellationToken)
    {
        if (edges.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO edges(edge_id, from_node_id, to_node_id, edge_type, weight, confidence,
                                         evidence, evidence_file_id, evidence_start_line, evidence_end_line,
                                         metadata_json)
            VALUES($id, $from, $to, $type, $weight, $confidence, $evidence, $file, $start, $end, $meta);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var fromParam = command.Parameters.Add("$from", SqliteType.Text);
        var toParam = command.Parameters.Add("$to", SqliteType.Text);
        var typeParam = command.Parameters.Add("$type", SqliteType.Text);
        var weightParam = command.Parameters.Add("$weight", SqliteType.Real);
        var confidenceParam = command.Parameters.Add("$confidence", SqliteType.Real);
        var evidenceParam = command.Parameters.Add("$evidence", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var metaParam = command.Parameters.Add("$meta", SqliteType.Text);

        foreach (var edge in edges)
        {
            var evidenceFileId = await ResolveFileIdAsync(connection, transaction, edge.EvidenceFilePath, fileIds, cancellationToken);
            idParam.Value = BuildEdgeId(edge.FromNodeId, edge.ToNodeId, edge.EdgeType, evidenceFileId);
            fromParam.Value = edge.FromNodeId;
            toParam.Value = edge.ToNodeId;
            typeParam.Value = edge.EdgeType;
            weightParam.Value = edge.Weight;
            confidenceParam.Value = edge.Confidence;
            evidenceParam.Value = (object?)edge.Evidence ?? DBNull.Value;
            fileParam.Value = (object?)evidenceFileId ?? DBNull.Value;
            startParam.Value = (object?)edge.EvidenceStartLine ?? DBNull.Value;
            endParam.Value = (object?)edge.EvidenceEndLine ?? DBNull.Value;
            metaParam.Value = (object?)edge.MetadataJson ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertRoutesAsync(IReadOnlyList<RouteRecord> routes, CancellationToken cancellationToken)
    {
        if (routes.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO routes(route_id, http_method, route_pattern, handler_symbol_id, file_id,
                                          start_line, end_line, source_kind, metadata_json)
            VALUES($id, $method, $pattern, $handler, $file, $start, $end, $source, $meta);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var methodParam = command.Parameters.Add("$method", SqliteType.Text);
        var patternParam = command.Parameters.Add("$pattern", SqliteType.Text);
        var handlerParam = command.Parameters.Add("$handler", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var sourceParam = command.Parameters.Add("$source", SqliteType.Text);
        var metaParam = command.Parameters.Add("$meta", SqliteType.Text);

        foreach (var route in routes)
        {
            var fileId = await ResolveFileIdAsync(connection, transaction, route.FilePath, fileIds, cancellationToken);
            if (fileId is null)
                continue;

            idParam.Value = route.RouteId;
            methodParam.Value = route.HttpMethod;
            patternParam.Value = route.RoutePattern;
            handlerParam.Value = (object?)route.HandlerSymbolId ?? DBNull.Value;
            fileParam.Value = fileId.Value;
            startParam.Value = route.StartLine;
            endParam.Value = route.EndLine;
            sourceParam.Value = route.SourceKind;
            metaParam.Value = (object?)route.MetadataJson ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertDiRegistrationsAsync(IReadOnlyList<DiRegistrationRecord> registrations, CancellationToken cancellationToken)
    {
        if (registrations.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO di_registrations(registration_id, service_symbol_id, implementation_symbol_id,
                                                    service_name, implementation_name, lifetime, file_id,
                                                    start_line, end_line, registration_kind, confidence, evidence)
            VALUES($id, $service_symbol, $impl_symbol, $service, $impl, $lifetime, $file, $start, $end,
                   $kind, $confidence, $evidence);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var serviceSymbolParam = command.Parameters.Add("$service_symbol", SqliteType.Text);
        var implSymbolParam = command.Parameters.Add("$impl_symbol", SqliteType.Text);
        var serviceParam = command.Parameters.Add("$service", SqliteType.Text);
        var implParam = command.Parameters.Add("$impl", SqliteType.Text);
        var lifetimeParam = command.Parameters.Add("$lifetime", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var kindParam = command.Parameters.Add("$kind", SqliteType.Text);
        var confidenceParam = command.Parameters.Add("$confidence", SqliteType.Real);
        var evidenceParam = command.Parameters.Add("$evidence", SqliteType.Text);

        foreach (var registration in registrations)
        {
            var fileId = await ResolveFileIdAsync(connection, transaction, registration.FilePath, fileIds, cancellationToken);
            if (fileId is null)
                continue;

            idParam.Value = registration.RegistrationId;
            serviceSymbolParam.Value = (object?)registration.ServiceSymbolId ?? DBNull.Value;
            implSymbolParam.Value = (object?)registration.ImplementationSymbolId ?? DBNull.Value;
            serviceParam.Value = registration.ServiceName;
            implParam.Value = (object?)registration.ImplementationName ?? DBNull.Value;
            lifetimeParam.Value = registration.Lifetime;
            fileParam.Value = fileId.Value;
            startParam.Value = registration.StartLine;
            endParam.Value = registration.EndLine;
            kindParam.Value = registration.RegistrationKind;
            confidenceParam.Value = registration.Confidence;
            evidenceParam.Value = (object?)registration.Evidence ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpsertOptionsBindingsAsync(IReadOnlyList<OptionsBindingRecord> bindings, CancellationToken cancellationToken)
    {
        if (bindings.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO options_bindings(binding_id, options_symbol_id, options_name, config_section,
                                                    file_id, start_line, end_line, binding_kind, confidence, evidence)
            VALUES($id, $symbol, $name, $section, $file, $start, $end, $kind, $confidence, $evidence);
            """;
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var symbolParam = command.Parameters.Add("$symbol", SqliteType.Text);
        var nameParam = command.Parameters.Add("$name", SqliteType.Text);
        var sectionParam = command.Parameters.Add("$section", SqliteType.Text);
        var fileParam = command.Parameters.Add("$file", SqliteType.Integer);
        var startParam = command.Parameters.Add("$start", SqliteType.Integer);
        var endParam = command.Parameters.Add("$end", SqliteType.Integer);
        var kindParam = command.Parameters.Add("$kind", SqliteType.Text);
        var confidenceParam = command.Parameters.Add("$confidence", SqliteType.Real);
        var evidenceParam = command.Parameters.Add("$evidence", SqliteType.Text);

        foreach (var binding in bindings)
        {
            var fileId = await ResolveFileIdAsync(connection, transaction, binding.FilePath, fileIds, cancellationToken);
            if (fileId is null)
                continue;

            idParam.Value = binding.BindingId;
            symbolParam.Value = (object?)binding.OptionsSymbolId ?? DBNull.Value;
            nameParam.Value = binding.OptionsName;
            sectionParam.Value = (object?)binding.ConfigSection ?? DBNull.Value;
            fileParam.Value = fileId.Value;
            startParam.Value = binding.StartLine;
            endParam.Value = binding.EndLine;
            kindParam.Value = binding.BindingKind;
            confidenceParam.Value = binding.Confidence;
            evidenceParam.Value = (object?)binding.Evidence ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteFileDataAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var fileIds = new Dictionary<string, long?>(StringComparer.Ordinal);
        var fileId = await ResolveFileIdAsync(connection, transaction, normalizedPath, fileIds, cancellationToken);
        if (fileId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        // Edges whose evidence is this file but whose endpoints live in other files do not cascade from
        // the node delete below, so remove them explicitly first. Deleting this file's nodes then cascades
        // (ON DELETE CASCADE) to any remaining edges that touch them.
        // Clear the FTS rows for this file's chunks before the chunks themselves are deleted; the FTS table
        // is managed manually and does not cascade.
        if (_ftsAvailable)
        {
            await using var ftsCommand = connection.CreateCommand();
            ftsCommand.Transaction = transaction;
            ftsCommand.CommandText =
                "DELETE FROM chunk_fts WHERE chunk_id IN (SELECT chunk_id FROM chunks WHERE file_id = $file);";
            ftsCommand.Parameters.AddWithValue("$file", fileId.Value);
            await ftsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM edges WHERE evidence_file_id = $file;
                DELETE FROM chunks WHERE file_id = $file;
                DELETE FROM symbols WHERE file_id = $file;
                DELETE FROM routes WHERE file_id = $file;
                DELETE FROM di_registrations WHERE file_id = $file;
                DELETE FROM options_bindings WHERE file_id = $file;
                DELETE FROM nodes WHERE file_id = $file;
                """;
            command.Parameters.AddWithValue("$file", fileId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (!_ftsAvailable || string.IsNullOrWhiteSpace(query.Text))
            return [];

        var match = BuildMatchExpression(query.Text);
        if (match.Length == 0)
            return [];

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // bm25() takes one weight per column in declaration order: chunk_id (unindexed, weight ignored),
        // path, name, symbols, signature, comments, body, subtokens, stems. Name/signature/symbols outrank path,
        // which outranks comments and body. The subtokens column (subword expansion of identifiers) is weighted
        // below the exact name but above the body, so an exact name match still wins over a subword match. The
        // stems column (Porter-stemmed identifiers and comments) is weighted lowest, as a fuzzy inflection bridge.
        // bm25 is lower-is-better, so the score is negated to make higher better.
        command.CommandText = """
            SELECT f.chunk_id, files.normalized_path, c.kind, c.name, c.start_line, c.end_line,
                   -bm25(chunk_fts, 0.0, 4.0, 3.0, 2.0, 1.5, 1.0, 0.7, 0.9, 0.5) AS score
            FROM chunk_fts f
            JOIN chunks c ON c.chunk_id = f.chunk_id
            JOIN files ON files.file_id = c.file_id
            WHERE chunk_fts MATCH $match
            ORDER BY score DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", query.Limit);

        var hits = new List<SearchHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new SearchHit(
                ChunkId: reader.GetString(0),
                FilePath: reader.GetString(1),
                Kind: reader.GetString(2),
                Name: reader.IsDBNull(3) ? null : reader.GetString(3),
                StartLine: reader.GetInt32(4),
                EndLine: reader.GetInt32(5),
                Score: reader.GetDouble(6)));
        }

        return hits;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SymbolListItem>> ListSymbolsAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.symbol_id, s.name, s.kind, s.fully_qualified_name, files.normalized_path,
                   s.start_line, s.is_public_api
            FROM symbols s
            JOIN files ON files.file_id = s.file_id
            ORDER BY s.is_public_api DESC, s.name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var items = new List<SymbolListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SymbolListItem(
                SymbolId: reader.GetString(0),
                Name: reader.GetString(1),
                Kind: reader.GetString(2),
                FullyQualifiedName: reader.GetString(3),
                FilePath: reader.GetString(4),
                StartLine: reader.GetInt32(5),
                IsPublicApi: reader.GetInt64(6) != 0));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SymbolListItem>> FindSymbolsByNameAsync(string nameFragment, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.symbol_id, s.name, s.kind, s.fully_qualified_name, files.normalized_path,
                   s.start_line, s.is_public_api
            FROM symbols s
            JOIN files ON files.file_id = s.file_id
            WHERE s.name LIKE '%' || $n || '%' COLLATE NOCASE
            ORDER BY s.is_public_api DESC, length(s.name), s.name COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$n", nameFragment);
        command.Parameters.AddWithValue("$limit", limit);

        var items = new List<SymbolListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new SymbolListItem(
                SymbolId: reader.GetString(0),
                Name: reader.GetString(1),
                Kind: reader.GetString(2),
                FullyQualifiedName: reader.GetString(3),
                FilePath: reader.GetString(4),
                StartLine: reader.GetInt32(5),
                IsPublicApi: reader.GetInt64(6) != 0));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RouteListItem>> ListRoutesAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.route_id, r.http_method, r.route_pattern, files.normalized_path, r.start_line
            FROM routes r
            JOIN files ON files.file_id = r.file_id
            ORDER BY r.route_pattern COLLATE NOCASE, r.http_method
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var items = new List<RouteListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new RouteListItem(
                RouteId: reader.GetString(0),
                HttpMethod: reader.GetString(1),
                RoutePattern: reader.GetString(2),
                FilePath: reader.GetString(3),
                StartLine: reader.GetInt32(4)));
        }

        return items;
    }

    /// <inheritdoc />
    public async Task<NodeRecord?> GetNodeAsync(string nodeId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = NodeSelect + " WHERE n.node_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", nodeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNode(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NodeRecord>> FindNodesByDisplayNameAsync(string displayName, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = NodeSelect + " WHERE n.display_name = $name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$name", displayName);
        return await ReadNodesAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NodeRecord>> GetNodesByFileAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = NodeSelect + " WHERE files.normalized_path = $p;";
        command.Parameters.AddWithValue("$p", normalizedPath);
        return await ReadNodesAsync(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SemanticEdgeRecord>> GetAllEdgesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.from_node_id, e.to_node_id, e.edge_type, e.weight, e.confidence, e.evidence, " +
            "files.normalized_path, e.evidence_start_line, e.evidence_end_line, e.metadata_json " +
            "FROM edges e LEFT JOIN files ON files.file_id = e.evidence_file_id;";

        var edges = new List<SemanticEdgeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            edges.Add(new SemanticEdgeRecord(
                FromNodeId: reader.GetString(0),
                ToNodeId: reader.GetString(1),
                EdgeType: reader.GetString(2),
                Weight: reader.GetDouble(3),
                Confidence: reader.GetDouble(4),
                Evidence: reader.IsDBNull(5) ? null : reader.GetString(5),
                EvidenceFilePath: reader.IsDBNull(6) ? null : reader.GetString(6),
                EvidenceStartLine: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                EvidenceEndLine: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                MetadataJson: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return edges;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SemanticEdgeRecord>> GetOutgoingEdgesAsync(string nodeId, CancellationToken cancellationToken) =>
        GetEdgesAsync("from_node_id", nodeId, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<SemanticEdgeRecord>> GetIncomingEdgesAsync(string nodeId, CancellationToken cancellationToken) =>
        GetEdgesAsync("to_node_id", nodeId, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileListItem>> FindFilesByPathAsync(string fragment, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT path, normalized_path, extension, is_test, is_generated FROM files " +
            "WHERE normalized_path LIKE '%' || $f || '%' COLLATE NOCASE ORDER BY length(normalized_path) LIMIT $limit;";
        command.Parameters.AddWithValue("$f", fragment);
        command.Parameters.AddWithValue("$limit", limit);

        var files = new List<FileListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            files.Add(new FileListItem(
                Path: reader.GetString(0),
                NormalizedPath: reader.GetString(1),
                Extension: reader.GetString(2),
                IsTest: reader.GetInt64(3) != 0,
                IsGenerated: reader.GetInt64(4) != 0));
        }

        return files;
    }

    /// <inheritdoc />
    public async Task<int> GetFileTokenEstimateAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COALESCE(SUM(c.reduced_token_estimate), 0) FROM chunks c " +
            "JOIN files f ON f.file_id = c.file_id WHERE f.normalized_path = $p;";
        command.Parameters.AddWithValue("$p", normalizedPath);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value ? (int)value : 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, string>> GetContentHashesAsync(
        IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = normalizedPaths.Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
            return result;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        // A parameterized IN list over the (small) candidate set; the caller passes at most the candidate cap.
        var placeholders = new string[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            placeholders[i] = "$p" + i;
            command.Parameters.AddWithValue(placeholders[i], names[i]);
        }

        command.CommandText =
            $"SELECT normalized_path, content_hash FROM files WHERE normalized_path IN ({string.Join(',', placeholders)});";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result[reader.GetString(0)] = reader.GetString(1);

        return result;
    }

    /// <inheritdoc />
    public async Task UpsertEmbeddingsAsync(IReadOnlyList<ChunkEmbeddingRecord> embeddings, CancellationToken cancellationToken)
    {
        if (embeddings.Count == 0)
            return;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO chunk_embeddings(chunk_id, dim, vector) VALUES($id, $dim, $vec);";
        var idParam = command.Parameters.Add("$id", SqliteType.Text);
        var dimParam = command.Parameters.Add("$dim", SqliteType.Integer);
        var vecParam = command.Parameters.Add("$vec", SqliteType.Blob);

        foreach (var embedding in embeddings)
        {
            // A chunk whose embedding row references a missing chunk would violate the foreign key, so skip
            // empty vectors (an empty or untokenizable chunk) rather than write a zero-length blob.
            if (embedding.Vector.Length == 0)
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            idParam.Value = embedding.ChunkId;
            dimParam.Value = embedding.Dimension;
            vecParam.Value = ToBlob(embedding.Vector);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChunkEmbedding>> GetEmbeddingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.chunk_id, files.normalized_path, c.name, e.dim, e.vector
            FROM chunk_embeddings e
            JOIN chunks c ON c.chunk_id = e.chunk_id
            JOIN files ON files.file_id = c.file_id;
            """;

        var embeddings = new List<ChunkEmbedding>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var dim = reader.GetInt32(3);
            var blob = (byte[])reader[4];
            embeddings.Add(new ChunkEmbedding(
                ChunkId: reader.GetString(0),
                FilePath: reader.GetString(1),
                Name: reader.IsDBNull(2) ? null : reader.GetString(2),
                Vector: FromBlob(blob, dim)));
        }

        return embeddings;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetFilesByLanguageAsync(string language, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT normalized_path FROM files WHERE language = $lang ORDER BY normalized_path;";
        command.Parameters.AddWithValue("$lang", language);

        var paths = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            paths.Add(reader.GetString(0));
        return paths;
    }

    /// <inheritdoc />
    public async Task UpsertCoChangesAsync(IReadOnlyList<CoChangeRecord> records, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // A re-mine is authoritative, so clear the prior table before writing the fresh set.
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM git_cochange;";
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "INSERT OR REPLACE INTO git_cochange(path_a, path_b, count, pmi, jaccard, last_seen_utc) " +
            "VALUES($a, $b, $count, $pmi, $jaccard, $last);";
        var aParam = command.Parameters.Add("$a", SqliteType.Text);
        var bParam = command.Parameters.Add("$b", SqliteType.Text);
        var countParam = command.Parameters.Add("$count", SqliteType.Integer);
        var pmiParam = command.Parameters.Add("$pmi", SqliteType.Real);
        var jaccardParam = command.Parameters.Add("$jaccard", SqliteType.Real);
        var lastParam = command.Parameters.Add("$last", SqliteType.Text);

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            aParam.Value = record.PathA;
            bParam.Value = record.PathB;
            countParam.Value = record.Count;
            pmiParam.Value = record.Pmi;
            jaccardParam.Value = record.Jaccard;
            lastParam.Value = (object?)record.LastSeenUtc ?? DBNull.Value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CoChangeRecord>> GetCoChangesForAsync(
        IReadOnlyCollection<string> normalizedPaths, CancellationToken cancellationToken)
    {
        if (normalizedPaths.Count == 0)
            return [];

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();

        // A parameterized IN list over the (small) seed set, matching either column of the pair.
        var names = new List<string>(normalizedPaths.Count);
        var i = 0;
        foreach (var path in normalizedPaths)
        {
            var name = $"$p{i++}";
            names.Add(name);
            command.Parameters.AddWithValue(name, path);
        }

        var inList = string.Join(", ", names);
        command.CommandText =
            $"SELECT path_a, path_b, count, pmi, jaccard, last_seen_utc FROM git_cochange " +
            $"WHERE path_a IN ({inList}) OR path_b IN ({inList});";

        var results = new List<CoChangeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new CoChangeRecord(
                PathA: reader.GetString(0),
                PathB: reader.GetString(1),
                Count: reader.GetInt32(2),
                Pmi: reader.GetDouble(3),
                Jaccard: reader.GetDouble(4),
                LastSeenUtc: reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return results;
    }

    // Embedding vectors are stored as a packed little-endian float32 blob; Buffer.BlockCopy is the fast,
    // allocation-light round trip and SQLite stores the bytes verbatim.
    private static byte[] ToBlob(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBlob(byte[] bytes, int dim)
    {
        var vector = new float[dim];
        Buffer.BlockCopy(bytes, 0, vector, 0, Math.Min(bytes.Length, dim * sizeof(float)));
        return vector;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _connectionFactory.ClearPool();
        return ValueTask.CompletedTask;
    }

    private const string NodeSelect =
        "SELECT n.node_id, n.kind, n.display_name, n.stable_key, files.normalized_path, n.symbol_id, " +
        "n.start_line, n.end_line, n.signature, n.metadata_json " +
        "FROM nodes n LEFT JOIN files ON files.file_id = n.file_id";

    private async Task<IReadOnlyList<SemanticEdgeRecord>> GetEdgesAsync(string column, string nodeId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT e.from_node_id, e.to_node_id, e.edge_type, e.weight, e.confidence, e.evidence, " +
            "files.normalized_path, e.evidence_start_line, e.evidence_end_line, e.metadata_json " +
            "FROM edges e LEFT JOIN files ON files.file_id = e.evidence_file_id " +
            $"WHERE e.{column} = $id;";
        command.Parameters.AddWithValue("$id", nodeId);

        var edges = new List<SemanticEdgeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            edges.Add(new SemanticEdgeRecord(
                FromNodeId: reader.GetString(0),
                ToNodeId: reader.GetString(1),
                EdgeType: reader.GetString(2),
                Weight: reader.GetDouble(3),
                Confidence: reader.GetDouble(4),
                Evidence: reader.IsDBNull(5) ? null : reader.GetString(5),
                EvidenceFilePath: reader.IsDBNull(6) ? null : reader.GetString(6),
                EvidenceStartLine: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                EvidenceEndLine: reader.IsDBNull(8) ? null : reader.GetInt32(8),
                MetadataJson: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return edges;
    }

    private static async Task<IReadOnlyList<NodeRecord>> ReadNodesAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var nodes = new List<NodeRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            nodes.Add(ReadNode(reader));
        return nodes;
    }

    private static NodeRecord ReadNode(SqliteDataReader reader) => new(
        NodeId: reader.GetString(0),
        Kind: reader.GetString(1),
        DisplayName: reader.GetString(2),
        StableKey: reader.GetString(3),
        FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
        SymbolId: reader.IsDBNull(5) ? null : reader.GetString(5),
        StartLine: reader.IsDBNull(6) ? null : reader.GetInt32(6),
        EndLine: reader.IsDBNull(7) ? null : reader.GetInt32(7),
        Signature: reader.IsDBNull(8) ? null : reader.GetString(8),
        MetadataJson: reader.IsDBNull(9) ? null : reader.GetString(9));

    // Build a safe FTS5 MATCH expression from free text. Each whitespace-separated run of letters/digits
    // becomes a quoted term (double quotes doubled per FTS5 escaping); terms are OR-ed for recall. Quoting
    // every term keeps user punctuation from being parsed as FTS5 operators.
    private static string BuildMatchExpression(string text)
    {
        var terms = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                terms.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
            terms.Add(current.ToString());

        // Expand each raw term with its subword parts so a compound query term (or a single prose word) matches
        // the subtokens column: "ApplyRoundingMode" adds apply, rounding, mode; "rounding" already matches the
        // subtokens of "ApplyRoundingMode". The raw term is kept so an exact name match still ranks first.
        var expanded = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            if (seen.Add(term))
                expanded.Add(term);
            // Subword parts match the subtokens column; the Porter stem of each part matches the stems column,
            // so an inflected query word (rounds) reaches an inflected indexed word (rounding). The raw term is
            // kept first so an exact name match still ranks above the subword and stemmed bridges.
            foreach (var part in IdentifierSplitter.Split(term))
            {
                if (seen.Add(part))
                    expanded.Add(part);
                var stem = PorterStemmer.Stem(part);
                if (seen.Add(stem))
                    expanded.Add(stem);
            }
        }

        return string.Join(" OR ", expanded.Select(t => $"\"{t.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    }

    // Derives a stable, bounded edge id from the components of the unique edge index so re-indexing the same
    // edge replaces the prior row rather than accumulating duplicates.
    private static string BuildEdgeId(string fromNodeId, string toNodeId, string edgeType, long? evidenceFileId)
    {
        var key = $"{fromNodeId}{toNodeId}{edgeType}{evidenceFileId?.ToString() ?? string.Empty}";
        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(key));
        return "edge:" + hash.ToString("x16");
    }

    private static async Task<long?> ResolveFileIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? normalizedPath,
        Dictionary<string, long?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(normalizedPath))
            return null;
        if (cache.TryGetValue(normalizedPath, out var cached))
            return cached;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT file_id FROM files WHERE normalized_path = $p LIMIT 1;";
        command.Parameters.AddWithValue("$p", normalizedPath);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var id = result is long value ? value : (long?)null;
        cache[normalizedPath] = id;
        return id;
    }

    private static async Task<long?> ResolveProjectIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? projectPath,
        Dictionary<string, long?> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(projectPath))
            return null;
        if (cache.TryGetValue(projectPath, out var cached))
            return cached;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT project_id FROM projects WHERE path = $p LIMIT 1;";
        command.Parameters.AddWithValue("$p", projectPath);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var id = result is long value ? value : (long?)null;
        cache[projectPath] = id;
        return id;
    }

    private static async Task ApplyDatabasePragmasAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = WorkspaceIndexSchema.CreatePragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT version FROM schema_version ORDER BY version DESC LIMIT 1;";
        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is long version ? (int)version : 0;
        }
        catch (SqliteException)
        {
            // No schema_version table yet: the store has never been initialized.
            return 0;
        }
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table};";
        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is long count ? (int)count : 0;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }
}
