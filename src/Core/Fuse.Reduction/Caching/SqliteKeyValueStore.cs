using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace Fuse.Reduction.Caching;

/// <summary>
///     A single-file SQLite key-value store using WAL mode.
/// </summary>
/// <remarks>
///     Reads use pooled connections for concurrency; writes are buffered and committed once via
///     <see cref="FlushAsync" />. A malformed database is deleted and recreated on open rather than failing the
///     run, because the file holds only derived cache data.
/// </remarks>
public sealed class SqliteKeyValueStore : IKeyValueStore
{
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
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
            Cache = SqliteCacheMode.Shared,
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

    /// <inheritdoc />
    public void Set(string store, string key, byte[] value) => _pending[(store, key)] = value;

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_pending.IsEmpty)
            return;

        var snapshot = _pending.ToArray();
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
        foreach (var entry in snapshot)
            _pending.TryRemove(entry.Key, out _);
    }

    /// <inheritdoc />
    public void Clear(string store)
    {
        foreach (var entry in _pending.Keys)
        {
            if (entry.Store == store)
                _pending.TryRemove(entry, out _);
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM kv WHERE store = $s";
        command.Parameters.AddWithValue("$s", store);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await FlushAsync();
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
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
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
            DeleteDatabaseFiles();
            CreateSchema();
        }
    }

    private void CreateSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA journal_mode = WAL;" +
            "PRAGMA synchronous = NORMAL;" +
            "CREATE TABLE IF NOT EXISTS kv(" +
            "  store TEXT NOT NULL," +
            "  key TEXT NOT NULL," +
            "  value BLOB NOT NULL," +
            "  PRIMARY KEY(store, key)) WITHOUT ROWID;";
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
