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
    public async Task SearchFindsCompoundNameBySingleSubword()
    {
        // S1: a prose query word matches a compound identifier through the subtokens field. "rounding" must
        // find ApplyRoundingMode even though unicode61 treats the whole name as one opaque token.
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/PriceCalculator.cs", "src/PriceCalculator.cs", ".cs", 80, 1, "hp")],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [new ChunkRecord("chunk:Price", "src/PriceCalculator.cs", "method", "kp", 1, 10, "thp", 30, 12,
                Name: "ApplyRoundingMode", Signature: "decimal ApplyRoundingMode(decimal value)")],
            CancellationToken.None);

        var hits = await _store.SearchAsync(new SearchQuery("rounding"), CancellationToken.None);

        Assert.Contains(hits, h => h.FilePath == "src/PriceCalculator.cs");
    }

    [Fact]
    public async Task ExactNameStillRanksAboveSubwordMatch()
    {
        await SeedAsync();
        // Add a file whose name only contains "Order" as a subword of a compound, so the exact "OrderService"
        // declaration must still rank first for the query "OrderService".
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/OrderProcessor.cs", "src/OrderProcessor.cs", ".cs", 90, 1, "hop")],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [new ChunkRecord("chunk:OrderProcessor", "src/OrderProcessor.cs", "type", "kop", 1, 15, "thop", 40, 18,
                Name: "OrderProcessor", Signature: "public class OrderProcessor")],
            CancellationToken.None);

        var hits = await _store.SearchAsync(new SearchQuery("OrderService"), CancellationToken.None);

        Assert.NotEmpty(hits);
        Assert.Equal("src/OrderService.cs", hits[0].FilePath);
    }

    [Fact]
    public async Task SearchFindsSnakeCaseAndDigitSubwords()
    {
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/Hashing.cs", "src/Hashing.cs", ".cs", 70, 1, "hh")],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [new ChunkRecord("chunk:Hash", "src/Hashing.cs", "method", "kh", 1, 8, "thh", 20, 10,
                Name: "compute_sha256_digest")],
            CancellationToken.None);

        Assert.Contains(await _store.SearchAsync(new SearchQuery("digest"), CancellationToken.None), h => h.FilePath == "src/Hashing.cs");
        Assert.Contains(await _store.SearchAsync(new SearchQuery("sha"), CancellationToken.None), h => h.FilePath == "src/Hashing.cs");
    }

    [Fact]
    public async Task SearchMatchesAcrossInflectionByStemming()
    {
        // S2: a stemmed query word matches an inflected code word. "calculate" finds a chunk named Calculation.
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/Calc.cs", "src/Calc.cs", ".cs", 60, 1, "hc")],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [new ChunkRecord("chunk:Calc", "src/Calc.cs", "type", "kc", 1, 8, "thc", 20, 10, Name: "Calculation")],
            CancellationToken.None);

        var hits = await _store.SearchAsync(new SearchQuery("calculate"), CancellationToken.None);

        Assert.Contains(hits, h => h.FilePath == "src/Calc.cs");
    }

    [Fact]
    public async Task SearchFindsFileByCommentProse()
    {
        // S2: a term that appears only in a comment localizes the file through the weighted comments field.
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/Widget.cs", "src/Widget.cs", ".cs", 60, 1, "hw")],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [new ChunkRecord("chunk:Widget", "src/Widget.cs", "type", "kw", 1, 8, "thw", 20, 10,
                Name: "Widget", Comments: "handles the warranty refund workflow")],
            CancellationToken.None);

        var hits = await _store.SearchAsync(new SearchQuery("warranty"), CancellationToken.None);

        Assert.Contains(hits, h => h.FilePath == "src/Widget.cs");
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
