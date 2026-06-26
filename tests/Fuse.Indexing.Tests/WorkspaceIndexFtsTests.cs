using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// P1.3: FTS5 indexing of chunks and ranked search.
public sealed class WorkspaceIndexFtsTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public void FullTextSearchIsAvailableInBundledRuntime()
    {
        // The bundled e_sqlite3 ships with FTS5; if this fails the publish smoke test (P11.1) would too.
        Assert.True(_store.FullTextSearchAvailable);
    }

    [Fact]
    public async Task SearchFindsChunkByName()
    {
        await SeedAsync();

        var hits = await _store.SearchAsync(new SearchQuery("OrderService"), CancellationToken.None);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Name == "OrderService" && h.FilePath == "src/OrderService.cs");
    }

    [Fact]
    public async Task SearchFindsChunkByBodyTerm()
    {
        await SeedAsync();

        var hits = await _store.SearchAsync(new SearchQuery("invoice"), CancellationToken.None);

        Assert.Contains(hits, h => h.FilePath == "src/BillingService.cs");
    }

    [Fact]
    public async Task SearchReturnsEmptyForUnmatchedQuery()
    {
        await SeedAsync();

        var hits = await _store.SearchAsync(new SearchQuery("nonexistentxyzterm"), CancellationToken.None);

        Assert.Empty(hits);
    }

    [Fact]
    public async Task ReindexedChunkDoesNotDuplicateInFts()
    {
        await SeedAsync();

        // Re-index the OrderService file: clear then re-upsert one chunk.
        await _store.DeleteFileDataAsync("src/OrderService.cs", CancellationToken.None);
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 120, 2, "h1b")],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [new ChunkRecord("chunk:OrderService", "src/OrderService.cs", "type", "k", 1, 20, "th", 50, 20, Name: "OrderService", Body: "places an order")],
            CancellationToken.None);

        var hits = await _store.SearchAsync(new SearchQuery("OrderService"), CancellationToken.None);
        Assert.Single(hits, h => h.ChunkId == "chunk:OrderService");
    }

    private async Task SeedAsync()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 120, 1, "h1"),
                new IndexedFileRecord("src/BillingService.cs", "src/BillingService.cs", ".cs", 140, 1, "h2"),
            ],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:OrderService", "src/OrderService.cs", "type", "k1", 1, 20, "th1", 50, 20,
                    Name: "OrderService", Signature: "public class OrderService", Body: "creates and places an order"),
                new ChunkRecord("chunk:BillingService", "src/BillingService.cs", "type", "k2", 1, 25, "th2", 60, 25,
                    Name: "BillingService", Signature: "public class BillingService", Body: "creates an invoice for billing"),
            ],
            CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
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
