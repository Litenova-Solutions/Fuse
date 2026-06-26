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

    /// <summary>The absolute path to the index database file.</summary>
    public string DatabasePath => _connectionFactory.DatabasePath;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_connectionFactory.DatabasePath)!);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await ApplyDatabasePragmasAsync(connection, cancellationToken);
        _schemaVersion = await WorkspaceIndexMigrator.MigrateAsync(connection, cancellationToken);
        _initialized = true;
        _logger?.LogDebug(
            "Workspace index initialized at {DatabasePath} (schema v{SchemaVersion}).",
            _connectionFactory.DatabasePath,
            _schemaVersion);
    }

    /// <inheritdoc />
    public async Task<WorkspaceIndexState> GetStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        var version = _initialized ? _schemaVersion : await ReadVersionAsync(connection, cancellationToken);
        var fileCount = await CountAsync(connection, "files", cancellationToken);
        var symbolCount = await CountAsync(connection, "symbols", cancellationToken);
        var status = fileCount == 0 ? WorkspaceIndexStatus.Cold : WorkspaceIndexStatus.Warm;
        return new WorkspaceIndexState(version, status, fileCount, symbolCount);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _connectionFactory.ClearPool();
        return ValueTask.CompletedTask;
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
