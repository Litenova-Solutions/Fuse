namespace Fuse.Indexing;

/// <summary>
///     The SQLite schema for the workspace semantic index: the target version and the DDL that
///     creates every Fuse-owned table and index.
/// </summary>
/// <remarks>
///     The schema is rebuilt from scratch whenever the on-disk version is below
///     <see cref="TargetVersion" /> (see <see cref="WorkspaceIndexMigrator" />); there is no
///     incremental migration path in V3. The full-text search virtual table is created separately
///     so a runtime lacking FTS5 can still build the relational schema and fall back.
/// </remarks>
public static class WorkspaceIndexSchema
{
    /// <summary>
    ///     The current schema version. The existing cache database carries a lower or absent version,
    ///     so it is dropped and rebuilt on the first V3 run.
    /// </summary>
    public const int TargetVersion = 10;

    /// <summary>
    ///     Database-level pragmas applied once at schema creation. WAL journaling and
    ///     <c>synchronous = NORMAL</c> persist with the file.
    /// </summary>
    public const string CreatePragmas =
        "PRAGMA journal_mode = WAL;" +
        "PRAGMA synchronous = NORMAL;";

    /// <summary>
    ///     Idempotent DDL creating every Fuse-owned relational table and index, plus the
    ///     <c>schema_version</c> bookkeeping table. Excludes the FTS5 virtual table.
    /// </summary>
    public const string CreateTablesDdl = """
        CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS index_meta(
          key TEXT PRIMARY KEY,
          value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS files(
          file_id INTEGER PRIMARY KEY,
          path TEXT NOT NULL UNIQUE,
          normalized_path TEXT NOT NULL UNIQUE,
          extension TEXT NOT NULL,
          size_bytes INTEGER NOT NULL,
          mtime_utc_ticks INTEGER NOT NULL,
          content_hash TEXT NOT NULL,
          project_id INTEGER NULL,
          is_generated INTEGER NOT NULL DEFAULT 0,
          is_test INTEGER NOT NULL DEFAULT 0,
          indexed_at_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_files_hash ON files(content_hash);
        CREATE INDEX IF NOT EXISTS idx_files_project ON files(project_id);
        CREATE INDEX IF NOT EXISTS idx_files_extension ON files(extension);

        CREATE TABLE IF NOT EXISTS projects(
          project_id INTEGER PRIMARY KEY,
          path TEXT NOT NULL UNIQUE,
          name TEXT NOT NULL,
          assembly_name TEXT NULL,
          target_framework TEXT NULL,
          project_hash TEXT NOT NULL,
          indexed_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS nodes(
          node_id TEXT PRIMARY KEY,
          kind TEXT NOT NULL,
          display_name TEXT NOT NULL,
          file_id INTEGER NULL,
          project_id INTEGER NULL,
          symbol_id TEXT NULL,
          stable_key TEXT NOT NULL,
          start_line INTEGER NULL,
          end_line INTEGER NULL,
          signature TEXT NULL,
          metadata_json TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_nodes_kind ON nodes(kind);
        CREATE INDEX IF NOT EXISTS idx_nodes_file ON nodes(file_id);
        CREATE INDEX IF NOT EXISTS idx_nodes_display ON nodes(display_name);
        CREATE INDEX IF NOT EXISTS idx_nodes_symbol ON nodes(symbol_id);

        CREATE TABLE IF NOT EXISTS symbols(
          symbol_id TEXT PRIMARY KEY,
          file_id INTEGER NOT NULL,
          project_id INTEGER NULL,
          kind TEXT NOT NULL,
          name TEXT NOT NULL,
          fully_qualified_name TEXT NOT NULL,
          metadata_name TEXT NULL,
          containing_type TEXT NULL,
          namespace TEXT NULL,
          assembly_name TEXT NULL,
          accessibility TEXT NULL,
          signature TEXT NULL,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          is_public_api INTEGER NOT NULL DEFAULT 0,
          FOREIGN KEY(file_id) REFERENCES files(file_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
        CREATE INDEX IF NOT EXISTS idx_symbols_fqn ON symbols(fully_qualified_name);
        CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_id);
        CREATE INDEX IF NOT EXISTS idx_symbols_kind ON symbols(kind);

        CREATE TABLE IF NOT EXISTS chunks(
          chunk_id TEXT PRIMARY KEY,
          file_id INTEGER NOT NULL,
          symbol_id TEXT NULL,
          kind TEXT NOT NULL,
          name TEXT NULL,
          stable_key TEXT NOT NULL,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          text_hash TEXT NOT NULL,
          token_estimate INTEGER NOT NULL,
          reduced_token_estimate INTEGER NOT NULL,
          signature TEXT NULL,
          outline TEXT NULL,
          FOREIGN KEY(file_id) REFERENCES files(file_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_chunks_file ON chunks(file_id);
        CREATE INDEX IF NOT EXISTS idx_chunks_symbol ON chunks(symbol_id);
        CREATE INDEX IF NOT EXISTS idx_chunks_kind ON chunks(kind);

        CREATE TABLE IF NOT EXISTS edges(
          edge_id TEXT PRIMARY KEY,
          from_node_id TEXT NOT NULL,
          to_node_id TEXT NOT NULL,
          edge_type TEXT NOT NULL,
          weight REAL NOT NULL,
          confidence REAL NOT NULL,
          evidence TEXT NULL,
          evidence_file_id INTEGER NULL,
          evidence_start_line INTEGER NULL,
          evidence_end_line INTEGER NULL,
          metadata_json TEXT NULL,
          FOREIGN KEY(from_node_id) REFERENCES nodes(node_id) ON DELETE CASCADE,
          FOREIGN KEY(to_node_id) REFERENCES nodes(node_id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_edges_from ON edges(from_node_id);
        CREATE INDEX IF NOT EXISTS idx_edges_to ON edges(to_node_id);
        CREATE INDEX IF NOT EXISTS idx_edges_type ON edges(edge_type);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_edges_unique
          ON edges(from_node_id, to_node_id, edge_type, evidence_file_id);

        CREATE TABLE IF NOT EXISTS routes(
          route_id TEXT PRIMARY KEY,
          http_method TEXT NOT NULL,
          route_pattern TEXT NOT NULL,
          handler_symbol_id TEXT NULL,
          file_id INTEGER NOT NULL,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          source_kind TEXT NOT NULL,
          metadata_json TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_routes_pattern ON routes(route_pattern);
        CREATE INDEX IF NOT EXISTS idx_routes_method ON routes(http_method);

        CREATE TABLE IF NOT EXISTS di_registrations(
          registration_id TEXT PRIMARY KEY,
          service_symbol_id TEXT NULL,
          implementation_symbol_id TEXT NULL,
          service_name TEXT NOT NULL,
          implementation_name TEXT NULL,
          lifetime TEXT NOT NULL,
          file_id INTEGER NOT NULL,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          registration_kind TEXT NOT NULL,
          confidence REAL NOT NULL,
          evidence TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_di_service ON di_registrations(service_name);
        CREATE INDEX IF NOT EXISTS idx_di_impl ON di_registrations(implementation_name);

        CREATE TABLE IF NOT EXISTS options_bindings(
          binding_id TEXT PRIMARY KEY,
          options_symbol_id TEXT NULL,
          options_name TEXT NOT NULL,
          config_section TEXT NULL,
          file_id INTEGER NOT NULL,
          start_line INTEGER NOT NULL,
          end_line INTEGER NOT NULL,
          binding_kind TEXT NOT NULL,
          confidence REAL NOT NULL,
          evidence TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_options_name ON options_bindings(options_name);
        CREATE INDEX IF NOT EXISTS idx_options_section ON options_bindings(config_section);

        CREATE TABLE IF NOT EXISTS git_cochange(
          path_a TEXT NOT NULL,
          path_b TEXT NOT NULL,
          count INTEGER NOT NULL,
          pmi REAL NOT NULL,
          jaccard REAL NOT NULL,
          last_seen_utc TEXT NULL,
          PRIMARY KEY(path_a, path_b)
        );
        CREATE INDEX IF NOT EXISTS idx_cochange_a ON git_cochange(path_a);
        CREATE INDEX IF NOT EXISTS idx_cochange_b ON git_cochange(path_b);
        """;

    /// <summary>
    ///     DDL for the FTS5 chunk search index. Created separately from <see cref="CreateTablesDdl" /> so a
    ///     runtime without FTS5 can still build the relational schema and fall back to no full-text search.
    /// </summary>
    /// <remarks>
    ///     Column order matters: the relevance weights in the search query (see the store's search path)
    ///     are positional. <c>chunk_id</c> is unindexed and used only to join hits back to the
    ///     <c>chunks</c> table.
    /// </remarks>
    public const string CreateFtsDdl =
        "CREATE VIRTUAL TABLE IF NOT EXISTS chunk_fts USING fts5(" +
        "  chunk_id UNINDEXED," +
        "  path, name, symbols, signature, comments, body," +
        "  tokenize = 'unicode61');";
}
