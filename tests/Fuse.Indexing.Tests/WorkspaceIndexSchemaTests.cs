using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// P1.1: schema creation and the drop-and-rebuild migration below version 10.
public sealed class WorkspaceIndexSchemaTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-tests", Guid.NewGuid().ToString("N"), "fuse.db");

    [Fact]
    public async Task InitializeCreatesSchemaAtTargetVersion()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        var state = await store.GetStateAsync(CancellationToken.None);

        Assert.Equal(WorkspaceIndexSchema.TargetVersion, state.SchemaVersion);
        Assert.Equal(WorkspaceIndexStatus.Cold, state.Status);
        Assert.Equal(0, state.FileCount);
        Assert.Equal(0, state.SymbolCount);
    }

    [Fact]
    public async Task InitializeIsIdempotentAndPreservesVersion()
    {
        await using (var first = new WorkspaceIndexStore(_databasePath))
            await first.InitializeAsync(CancellationToken.None);

        await using var second = new WorkspaceIndexStore(_databasePath);
        await second.InitializeAsync(CancellationToken.None);
        var state = await second.GetStateAsync(CancellationToken.None);

        Assert.Equal(WorkspaceIndexSchema.TargetVersion, state.SchemaVersion);
    }

    [Fact]
    public async Task InitializeDropsLegacyTablesAndRebuilds()
    {
        // Simulate a pre-V3 cache database: a stale kv table and no schema_version row.
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE kv(store TEXT, key TEXT, value BLOB);" +
                "INSERT INTO kv(store, key, value) VALUES('s', 'k', x'00');";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_databasePath}"));

        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        Assert.Equal(WorkspaceIndexSchema.TargetVersion,
            (await store.GetStateAsync(CancellationToken.None)).SchemaVersion);
        Assert.False(await TableExistsAsync("kv"), "legacy kv table should be dropped on rebuild");
        Assert.True(await TableExistsAsync("files"), "files table should exist after rebuild");
        Assert.True(await TableExistsAsync("edges"), "edges table should exist after rebuild");
    }

    private async Task<bool> TableExistsAsync(string name)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n;";
        command.Parameters.AddWithValue("$n", name);
        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        return result is long count && count > 0;
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_databasePath);
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_databasePath}"));
        try
        {
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
