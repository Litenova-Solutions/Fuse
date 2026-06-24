using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Fuse.Reduction.Caching;

/// <summary>
///     A single-file SQLite key-value store using WAL mode.
/// </summary>
/// <remarks>
///     Reads use pooled connections for concurrency; writes are buffered and committed once via
///     <see cref="FlushAsync" />. A malformed database is deleted and recreated on open, read, or flush rather than
///     failing the run, because the file holds only derived cache data. Concurrent runs share the same file via
///     WAL; <c>busy_timeout</c> and flush retries on <c>SQLITE_BUSY</c> tolerate overlapping writers.
/// </remarks>
public sealed class SqliteKeyValueStore : IKeyValueStore
{
    private const int BusyTimeoutSeconds = 30;
    private const int MaxFlushAttempts = 5;
    private const int SqliteError = 1;
    private const int SqliteBusy = 5;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADb = 26;

    // Idempotent DDL for the single key-value table. Shared by schema creation and the flush path, which runs it
    // defensively so a pooled or shared-cache connection that has not yet observed a just-recreated database
    // cannot fail an insert with "no such table: kv".
    private const string CreateTableDdl =
        "CREATE TABLE IF NOT EXISTS kv(" +
        "  store TEXT NOT NULL," +
        "  key TEXT NOT NULL," +
        "  value BLOB NOT NULL," +
        "  PRIMARY KEY(store, key)) WITHOUT ROWID;";

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<(string Store, string Key), byte[]> _pending = new();

    /// <summary>
    ///     Opens or creates the database, recovering by recreating a corrupt file.
    /// </summary>
    /// <param name="databasePath">The absolute path to the SQLite database file.</param>
    public SqliteKeyValueStore(string databasePath)
    {
        _databasePath = databasePath;
        // Private cache (the default), not shared: WAL mode plus connection pooling already give cross-connection
        // concurrency, and a process-wide shared cache can outlive a corrupt-database delete-and-recreate, leaving
        // a fresh connection attached to a stale empty cache that does not show the recreated schema (an
        // intermittent "no such table: kv"). A private cache makes every connection read the actual file state.
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
            DefaultTimeout = BusyTimeoutSeconds,
        }.ToString();
        Initialize();
    }

    /// <inheritdoc />
    public bool TryGet(string store, string key, out byte[]? value)
    {
        // Read-your-writes: buffered entries are visible before they are flushed.
        if (_pending.TryGetValue((store, key), out var buffered))
        {
            value = buffered;
            return true;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM kv WHERE store = $s AND key = $k";
                command.Parameters.AddWithValue("$s", store);
                command.Parameters.AddWithValue("$k", key);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    value = (byte[])reader["value"];
                    return true;
                }

                value = null;
                return false;
            }
            catch (SqliteException ex) when (IsCorruptDatabaseError(ex) && attempt == 0)
            {
                RecoverCorruptDatabase();
            }
            catch (SqliteException ex) when (IsMissingTableError(ex) && attempt == 0)
            {
                CreateSchema();
            }
        }

        value = null;
        return false;
    }

    /// <inheritdoc />
    public void Set(string store, string key, byte[] value) => _pending[(store, key)] = value;

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_pending.IsEmpty)
            return;

        var snapshot = _pending.ToArray();
        for (var attempt = 0; attempt < MaxFlushAttempts; attempt++)
        {
            try
            {
                await FlushSnapshotAsync(snapshot, cancellationToken);
                // Remove only entries whose value is still the one just flushed. A concurrent Set on the same
                // key replaces the value with a different array reference, so the KeyValuePair overload (value
                // equality, reference equality for byte[]) leaves that newer value pending for the next flush
                // instead of dropping it. Removing by key alone would lose the concurrent update.
                foreach (var entry in snapshot)
                    ((ICollection<KeyValuePair<(string Store, string Key), byte[]>>)_pending).Remove(entry);
                return;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteBusy && attempt < MaxFlushAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
            }
            catch (SqliteException ex) when (IsCorruptDatabaseError(ex) && attempt < MaxFlushAttempts - 1)
            {
                RecoverCorruptDatabase();
            }
            catch (SqliteException ex) when (IsMissingTableError(ex) && attempt < MaxFlushAttempts - 1)
            {
                CreateSchema();
            }
        }
    }

    /// <inheritdoc />
    public void Clear(string store)
    {
        foreach (var entry in _pending.Keys)
        {
            if (entry.Store == store)
                _pending.TryRemove(entry, out _);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM kv WHERE store = $s";
                command.Parameters.AddWithValue("$s", store);
                command.ExecuteNonQuery();
                return;
            }
            catch (SqliteException ex) when (IsCorruptDatabaseError(ex) && attempt == 0)
            {
                RecoverCorruptDatabase();
            }
            catch (SqliteException ex) when (IsMissingTableError(ex) && attempt == 0)
            {
                CreateSchema();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await FlushAsync();
        }
        catch (SqliteException)
        {
            // Derived cache data; flush on dispose is best-effort.
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        }
    }

    private async Task FlushSnapshotAsync(
        KeyValuePair<(string Store, string Key), byte[]>[] snapshot,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO kv(store, key, value) VALUES($s, $k, $v)";
        var storeParam = command.Parameters.Add("$s", SqliteType.Text);
        var keyParam = command.Parameters.Add("$k", SqliteType.Text);
        var valueParam = command.Parameters.Add("$v", SqliteType.Blob);

        foreach (var ((store, key), value) in snapshot)
        {
            storeParam.Value = store;
            keyParam.Value = key;
            valueParam.Value = value;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private void Initialize()
    {
        try
        {
            CreateSchema();
        }
        catch (SqliteException)
        {
            // A malformed database is derived data; delete and recreate rather than fail the run.
            RecoverCorruptDatabase();
        }
    }

    private static bool IsCorruptDatabaseError(SqliteException ex) =>
        ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADb;

    // After a corrupt database is deleted and recreated, connection pooling and SQLite shared-cache mode can
    // briefly hand an operation a connection whose view of the just-recreated database does not yet show the
    // table, surfacing as a generic error (code 1) with a "no such table" message. This is not corruption: the
    // file is a valid, empty database, so the recovery is to (re)create the schema rather than delete the file.
    private static bool IsMissingTableError(SqliteException ex) =>
        ex.SqliteErrorCode == SqliteError && ex.Message.Contains("no such table", StringComparison.Ordinal);

    private void RecoverCorruptDatabase()
    {
        SqliteConnection.ClearAllPools();
        DeleteDatabaseFiles();
        CreateSchema();
    }

    private void CreateSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA busy_timeout = {BusyTimeoutSeconds * 1000};" +
            "PRAGMA journal_mode = WAL;" +
            "PRAGMA synchronous = NORMAL;" +
            CreateTableDdl;
        command.ExecuteNonQuery();
    }

    private void DeleteDatabaseFiles()
    {
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
