using Microsoft.Data.Sqlite;

namespace Fuse.Indexing;

/// <summary>
///     Creates SQLite connections to the workspace semantic index database, applying the
///     per-connection pragmas the index relies on.
/// </summary>
/// <remarks>
///     WAL journaling and <c>synchronous = NORMAL</c> are database-level settings applied once when
///     the schema is created (see <see cref="WorkspaceIndexSchema" />); <c>busy_timeout</c> and
///     <c>foreign_keys</c> are connection-level and must be set on every connection, so they are
///     applied here. Connections are pooled so the warm host can fan out reads without reopening the
///     file each call.
/// </remarks>
public sealed class WorkspaceIndexConnectionFactory
{
    private const int BusyTimeoutMilliseconds = 30000;

    private readonly string _connectionString;

    /// <summary>
    ///     Initializes a new instance of the <see cref="WorkspaceIndexConnectionFactory" /> class.
    /// </summary>
    /// <param name="databasePath">The absolute path to the index database file.</param>
    public WorkspaceIndexConnectionFactory(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
            DefaultTimeout = BusyTimeoutMilliseconds / 1000,
        }.ToString();
    }

    /// <summary>The absolute path to the index database file.</summary>
    public string DatabasePath { get; }

    /// <summary>The connection string used for every connection this factory opens.</summary>
    public string ConnectionString => _connectionString;

    /// <summary>
    ///     Opens a new connection and applies the per-connection pragmas
    ///     (<c>busy_timeout</c>, <c>foreign_keys = ON</c>).
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the open.</param>
    /// <returns>An open <see cref="SqliteConnection" />.</returns>
    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};" +
            "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    ///     Clears the pooled connections for this database. Used before deleting or recreating the file.
    /// </summary>
    public void ClearPool() => SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
}
