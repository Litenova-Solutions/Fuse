using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// P1.4: end-to-end Phase 1 flow over a small workspace - insert files/symbols/chunks, FTS finds
// OrderService, reindex a changed file, edges persist across a store reopen, WAL enabled, safe disposal.
public sealed class WorkspaceIndexIntegrationTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-tests", Guid.NewGuid().ToString("N"), "fuse.db");

    [Fact]
    public async Task FullPhaseOneFlow()
    {
        // 1. Index a small workspace.
        await using (var store = new WorkspaceIndexStore(_databasePath))
        {
            await store.InitializeAsync(CancellationToken.None);
            await IndexOrderWorkspaceAsync(store);

            var state = await store.GetStateAsync(CancellationToken.None);
            Assert.Equal(2, state.FileCount);
            Assert.Equal(WorkspaceIndexStatus.Warm, state.Status);

            // 2. FTS finds OrderService.
            var hits = await store.SearchAsync(new SearchQuery("OrderService"), CancellationToken.None);
            Assert.Contains(hits, h => h.Name == "OrderService");

            // 3. Edges persist.
            Assert.Equal(1, await CountAsync("edges"));
        }

        // 4. Reopen the store (proves the file survives disposal) and confirm data is still there.
        await using (var reopened = new WorkspaceIndexStore(_databasePath))
        {
            await reopened.InitializeAsync(CancellationToken.None);
            Assert.Equal(WorkspaceIndexSchema.TargetVersion,
                (await reopened.GetStateAsync(CancellationToken.None)).SchemaVersion);
            Assert.Equal(1, await CountAsync("edges"));

            // 5. Reindex a changed file: clear and re-upsert with new content; old symbols replaced.
            await reopened.DeleteFileDataAsync("src/OrderService.cs", CancellationToken.None);
            await reopened.UpsertFilesAsync(
                [new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 300, 9, "neworderhash")],
                CancellationToken.None);
            await reopened.UpsertSymbolsAsync(
                [new SymbolRecord("symbol:App.OrderService", "src/OrderService.cs", "type", "OrderService", "App.OrderService", StartLine: 1, EndLine: 40)],
                CancellationToken.None);
            await reopened.UpsertChunksAsync(
                [new ChunkRecord("chunk:OrderService", "src/OrderService.cs", "type", "k", 1, 40, "th-new", 80, 30, Name: "OrderService", Body: "rewritten order service")],
                CancellationToken.None);

            Assert.Equal("neworderhash",
                await TextScalarAsync("SELECT content_hash FROM files WHERE normalized_path = 'src/OrderService.cs';"));
            Assert.Equal(2, (await reopened.GetStateAsync(CancellationToken.None)).FileCount);
        }
    }

    [Fact]
    public async Task DatabaseUsesWalJournalMode()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)await command.ExecuteScalarAsync(CancellationToken.None);

        Assert.Equal("wal", mode, ignoreCase: true);
    }

    private static async Task IndexOrderWorkspaceAsync(WorkspaceIndexStore store)
    {
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

    private async Task<long> CountAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table};";
        var result = await command.ExecuteScalarAsync(CancellationToken.None);
        return result is long value ? value : 0;
    }

    private async Task<string?> TextScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None) as string;
    }

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_databasePath);
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
