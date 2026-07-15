using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// F-001: integration proving derived-cache recovery does not drop semantic index tables when stores are split.
public sealed class SplitStoreIndexIsolationTests : IDisposable
{
    private readonly string _repoRoot;

    public SplitStoreIndexIsolationTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "fuse-split-index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
        Directory.CreateDirectory(Path.Combine(_repoRoot, ".git"));
    }

    [Fact]
    public async Task CorruptCacheRecovery_PreservesIndexSymbolsAndOtherTables()
    {
        var indexPath = FuseStorePaths.ResolveDatabasePath(_repoRoot);
        var cachePath = FuseStorePaths.ResolveCacheDatabasePath(_repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);

        await SeedSemanticIndexAsync(indexPath);

        await using (var cache = new FuseStoreFactory().Open(_repoRoot))
        {
            cache.Set("reduction", "cached-entry", [7, 8, 9]);
            await cache.FlushAsync();
        }

        var indexTablesBefore = await ListTablesAsync(indexPath);
        var symbolCountBefore = await CountAsync(indexPath, "symbols");
        var fileCountBefore = await CountAsync(indexPath, "files");
        var edgeCountBefore = await CountAsync(indexPath, "edges");

        File.WriteAllText(cachePath, "not a sqlite database");

        await using (var cache = new FuseStoreFactory().Open(_repoRoot))
        {
            cache.Set("reduction", "rebuilt-entry", [1]);
            Assert.True(cache.TryGet("reduction", "rebuilt-entry", out var value));
            Assert.Equal([1], value);
            await cache.FlushAsync();
        }

        Assert.Equal(indexTablesBefore, await ListTablesAsync(indexPath));
        Assert.Equal(symbolCountBefore, await CountAsync(indexPath, "symbols"));
        Assert.Equal(fileCountBefore, await CountAsync(indexPath, "files"));
        Assert.Equal(edgeCountBefore, await CountAsync(indexPath, "edges"));
        Assert.Contains("OrderService", await ReadSymbolNamesAsync(indexPath));

        Assert.Equal(["kv"], await ListTablesAsync(cachePath));

        await using var reopened = new WorkspaceIndexStore(indexPath);
        await reopened.InitializeAsync(CancellationToken.None);

        var state = await reopened.GetStateAsync(CancellationToken.None);
        Assert.Equal(WorkspaceIndexStatus.Warm, state.Status);
        Assert.Equal(2, state.FileCount);
        Assert.Equal(2, state.SymbolCount);

        var hits = await reopened.SearchAsync(new SearchQuery("OrderService"), CancellationToken.None);
        Assert.Contains(hits, hit => hit.Name == "OrderService");
    }

    private static async Task SeedSemanticIndexAsync(string indexPath)
    {
        await using var store = new WorkspaceIndexStore(indexPath);
        await store.InitializeAsync(CancellationToken.None);

        await store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 120, 1, "h1"),
                new IndexedFileRecord("src/IOrderService.cs", "src/IOrderService.cs", ".cs", 40, 1, "h2"),
            ],
            CancellationToken.None);
        await store.UpsertSymbolsAsync(
            [
                new SymbolRecord("symbol:App.OrderService", "src/OrderService.cs", "type", "OrderService", "App.OrderService", StartLine: 1, EndLine: 20),
                new SymbolRecord("symbol:App.IOrderService", "src/IOrderService.cs", "interface", "IOrderService", "App.IOrderService", StartLine: 1, EndLine: 5),
            ],
            CancellationToken.None);
        await store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:OrderService", "src/OrderService.cs", "type", "k1", 1, 20, "th1", 50, 20, Name: "OrderService", Body: "implements IOrderService"),
                new ChunkRecord("chunk:IOrderService", "src/IOrderService.cs", "interface", "k2", 1, 5, "th2", 12, 8, Name: "IOrderService", Body: "order service contract"),
            ],
            CancellationToken.None);
        await store.UpsertNodesAsync(
            [
                new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "src/IOrderService.cs"),
                new NodeRecord("type:App.OrderService", "type", "OrderService", "App.OrderService", "src/OrderService.cs"),
            ],
            CancellationToken.None);
        await store.UpsertEdgesAsync(
            [new SemanticEdgeRecord("type:App.IOrderService", "type:App.OrderService", "di_resolves_to", 0.95, 0.95, EvidenceFilePath: "src/OrderService.cs")],
            CancellationToken.None);
    }

    private static async Task<long> CountAsync(string databasePath, string table)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table};";
        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        return result is long value ? value : 0;
    }

    private static async Task<string[]> ReadSymbolNamesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM symbols ORDER BY name;";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
            names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static async Task<string[]> ListTablesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        while (await reader.ReadAsync(CancellationToken.None))
            tables.Add(reader.GetString(0));
        return tables.ToArray();
    }

    public void Dispose()
    {

        if (!Directory.Exists(_repoRoot))
            return;

        try
        {
            Directory.Delete(_repoRoot, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
