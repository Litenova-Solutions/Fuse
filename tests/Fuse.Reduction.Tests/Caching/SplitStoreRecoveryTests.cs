using Fuse.Reduction.Caching;
using Microsoft.Data.Sqlite;

namespace Fuse.Reduction.Tests.Caching;

// F-001: derived-cache recovery must never delete the semantic index when index and cache are split.
public sealed class SplitStoreRecoveryTests : IDisposable
{
    private readonly string _repoRoot;

    public SplitStoreRecoveryTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "fuse-split-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(Path.Combine(_repoRoot, ".git"));
    }

    [Fact]
    public async Task CorruptCacheDatabase_RecreatesKvSchemaOnly()
    {
        var cachePath = FuseStorePaths.ResolveCacheDatabasePath(_repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        await using (var store = new SqliteKeyValueStore(cachePath))
        {
            store.Set("reduction", "entry", [1, 2, 3]);
            await store.FlushAsync();
        }

        Assert.Equal(["kv"], await ListTablesAsync(cachePath));

        File.WriteAllText(cachePath, "not a sqlite database");

        await using (var store = new SqliteKeyValueStore(cachePath))
        {
            store.Set("reduction", "recovered", [9]);
            Assert.True(store.TryGet("reduction", "recovered", out var value));
            Assert.Equal([9], value);
            await store.FlushAsync();
        }

        Assert.Equal(["kv"], await ListTablesAsync(cachePath));
    }

    [Fact]
    public async Task CorruptCacheDatabase_DoesNotDeleteSiblingIndexDatabase()
    {
        var indexPath = FuseStorePaths.ResolveDatabasePath(_repoRoot);
        var cachePath = FuseStorePaths.ResolveCacheDatabasePath(_repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);

        await SeedIndexSymbolsAsync(indexPath, "OrderService");

        await using (var store = new SqliteKeyValueStore(cachePath))
        {
            store.Set("reduction", "entry", [4]);
            await store.FlushAsync();
        }

        File.WriteAllText(cachePath, "not a sqlite database");

        await using (var store = new SqliteKeyValueStore(cachePath))
        {
            store.Set("reduction", "after-recovery", [5]);
            await store.FlushAsync();
        }

        Assert.Equal(1, await CountIndexSymbolsAsync(indexPath));
        Assert.Equal("OrderService", await ReadSymbolNameAsync(indexPath));
        Assert.Equal(["kv"], await ListTablesAsync(cachePath));
    }

    [Fact]
    public async Task CorruptIndexDatabase_RefusesDeleteAndLeavesFileUntouched()
    {
        var indexPath = FuseStorePaths.ResolveDatabasePath(_repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);

        await SeedIndexSymbolsAsync(indexPath, "PersistedSymbol");

        await using (var store = new SqliteKeyValueStore(indexPath))
        {
            store.Set("derived", "probe", [1]);
            await store.FlushAsync();
        }

        const string corruptPayload = "not a sqlite database";
        File.WriteAllText(indexPath, corruptPayload);

        await using (var store = new SqliteKeyValueStore(indexPath))
        {
            Assert.False(store.TryGet("derived", "probe", out _));
        }

        Assert.Equal(corruptPayload, await File.ReadAllTextAsync(indexPath));
    }

    private static async Task SeedIndexSymbolsAsync(string indexPath, string symbolName)
    {
        await using (var connection = new SqliteConnection($"Data Source={indexPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE symbols(symbol_id TEXT PRIMARY KEY, name TEXT NOT NULL);" +
                "INSERT INTO symbols(symbol_id, name) VALUES('symbol:test', $name);";
            command.Parameters.AddWithValue("$name", symbolName);
            await command.ExecuteNonQueryAsync();
        }

        ClearDirectPool(indexPath);
    }

    private static async Task<long> CountIndexSymbolsAsync(string indexPath)
    {
        object? result;
        await using (var connection = new SqliteConnection($"Data Source={indexPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM symbols;";
            result = await command.ExecuteScalarAsync();
        }

        ClearDirectPool(indexPath);
        return result is long value ? value : 0;
    }

    private static async Task<string?> ReadSymbolNameAsync(string indexPath)
    {
        string? result;
        await using (var connection = new SqliteConnection($"Data Source={indexPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM symbols LIMIT 1;";
            result = await command.ExecuteScalarAsync() as string;
        }

        ClearDirectPool(indexPath);
        return result;
    }

    private static async Task<string[]> ListTablesAsync(string databasePath)
    {
        var tables = new List<string>();
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));
        }

        ClearDirectPool(databasePath);
        return tables.ToArray();
    }

    public void Dispose()
    {

        ClearDirectPool(FuseStorePaths.ResolveCacheDatabasePath(_repoRoot));
        ClearDirectPool(FuseStorePaths.ResolveDatabasePath(_repoRoot));

        if (!Directory.Exists(_repoRoot))
            return;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(_repoRoot, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void ClearDirectPool(string databasePath) =>
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={databasePath}"));
}
