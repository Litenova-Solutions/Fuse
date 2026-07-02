using Microsoft.Data.Sqlite;

namespace Fuse.Indexing;

/// <summary>
///     Brings an index database to <see cref="WorkspaceIndexSchema.TargetVersion" />.
/// </summary>
/// <remarks>
///     V3 has no backward-compatibility requirement, so there is no incremental migration: if the
///     on-disk version is below the target (the existing cache database carries a lower or absent
///     version), every Fuse-owned table is dropped and the schema is rebuilt. Dropping is driven off
///     <c>sqlite_master</c> so a stale table from an earlier schema (for example the old <c>kv</c>
///     cache table) is removed too, not just the tables this version knows by name.
/// </remarks>
public static class WorkspaceIndexMigrator
{
    /// <summary>
    ///     Reads the current schema version and, if it is below the target, drops all Fuse-owned
    ///     tables and rebuilds the schema. A database already at the target version is left untouched.
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="cancellationToken">A token to cancel the migration.</param>
    /// <returns>The schema version in effect after migration.</returns>
    public static async Task<int> MigrateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await EnsureVersionTableAsync(connection, cancellationToken);
        var currentVersion = await ReadVersionAsync(connection, cancellationToken);
        if (currentVersion >= WorkspaceIndexSchema.TargetVersion)
            return currentVersion;

        await RebuildAsync(connection, cancellationToken);
        return WorkspaceIndexSchema.TargetVersion;
    }

    /// <summary>
    ///     Drops every Fuse-owned table and recreates the schema at <see cref="WorkspaceIndexSchema.TargetVersion" />.
    /// </summary>
    /// <param name="connection">An open connection to the index database.</param>
    /// <param name="cancellationToken">A token to cancel the rebuild.</param>
    /// <returns>A task that completes when the schema has been rebuilt.</returns>
    /// <remarks>
    ///     Used both by <see cref="MigrateAsync" /> on a schema-version bump and by the store when the index was
    ///     written by an incompatible Fuse build (see <see cref="FuseBuildInfo.IsCompatible" />). The rebuild
    ///     empties the index; the caller re-indexes to repopulate it.
    /// </remarks>
    public static async Task RebuildAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await DropAllObjectsAsync(connection, transaction, cancellationToken);
        await ExecuteAsync(connection, transaction, WorkspaceIndexSchema.CreateTablesDdl, cancellationToken);
        await SetVersionAsync(connection, transaction, WorkspaceIndexSchema.TargetVersion, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureVersionTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version ORDER BY version DESC LIMIT 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long version ? (int)version : 0;
    }

    // Drop every user-defined object so a database from any earlier schema is rebuilt cleanly. Foreign keys are
    // disabled for the drop so table order does not matter; the version table is preserved and reset afterward.
    private static async Task DropAllObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var objects = new List<(string Type, string Name, bool IsVirtual)>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText =
                "SELECT type, name, sql FROM sqlite_master " +
                "WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' AND name <> 'schema_version';";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var sql = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                var isVirtual = sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase);
                objects.Add((reader.GetString(0), reader.GetString(1), isVirtual));
            }
        }

        await ExecuteAsync(connection, transaction, "PRAGMA foreign_keys = OFF;", cancellationToken);
        // Drop virtual (for example FTS5) tables first: that removes their backing shadow tables, so the
        // remaining DROP ... IF EXISTS for those shadows becomes a harmless no-op rather than an
        // "use DROP TABLE to delete the fts5 table" error.
        foreach (var (type, name, _) in objects.OrderByDescending(o => o.IsVirtual))
        {
            var quoted = name.Replace("\"", "\"\"", StringComparison.Ordinal);
            await ExecuteAsync(connection, transaction, $"DROP {type.ToUpperInvariant()} IF EXISTS \"{quoted}\";", cancellationToken);
        }

        await ExecuteAsync(connection, transaction, "DELETE FROM schema_version;", cancellationToken);
        await ExecuteAsync(connection, transaction, "PRAGMA foreign_keys = ON;", cancellationToken);
    }

    private static async Task SetVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO schema_version(version) VALUES($v);";
        command.Parameters.AddWithValue("$v", version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
