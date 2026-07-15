using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// R23: a version/schema mismatch (or a store missing chunk_fts) must rebuild to a working, searchable index -
// FTS re-probed, chunk_fts recreated, mode stamped - and a search against a chunk_fts-less store must surface a
// clean operational signal, never a raw "no such table: chunk_fts".
public sealed class WorkspaceIndexSearchRebuildTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-r23", Guid.NewGuid().ToString("N"), "fuse.db");

    [Fact]
    public async Task Search_WhenChunkFtsDropped_ThrowsSearchIndexUnavailable_NotRawSqlite()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);
        await SeedSearchableChunkAsync(store);

        // Sanity: search works while chunk_fts is present.
        var hits = await store.SearchAsync(new SearchQuery("OrderProcessor", 10), CancellationToken.None);
        Assert.NotEmpty(hits);

        // Simulate the reproduction: chunk_fts goes missing under a store that still believes FTS is available.
        DropChunkFts();

        var ex = await Assert.ThrowsAsync<SearchIndexUnavailableException>(
            () => store.SearchAsync(new SearchQuery("OrderProcessor", 10), CancellationToken.None));
        Assert.Contains("rebuilding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetState_WhenChunkFtsMissing_ReportsFtsUnavailable_AndChunkCount()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);
        await SeedSearchableChunkAsync(store);

        var before = await store.GetStateAsync(CancellationToken.None);
        Assert.True(before.FtsAvailable);
        Assert.True(before.ChunkCount > 0);

        DropChunkFts();

        var after = await store.GetStateAsync(CancellationToken.None);
        // FTS availability reconciles with the actual table so the status line and body never disagree.
        Assert.False(after.FtsAvailable);
    }

    [Fact]
    public async Task Initialize_PopulatedStoreMissingChunkFts_RebuildsDerivedData()
    {
        await using (var seed = new WorkspaceIndexStore(_databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await SeedSearchableChunkAsync(seed);
        }

        DropChunkFts();

        await using var reopened = new WorkspaceIndexStore(_databasePath);
        var outcome = await reopened.InitializeAsync(CancellationToken.None);

        Assert.True(outcome.RebuiltEmptyStore);
        Assert.Contains("search index", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await reopened.OpenForReadAsync(CancellationToken.None));
        Assert.True(await ChunkFtsExistsAsync());
    }

    [Fact]
    public async Task VersionMismatchRebuild_ProducesSearchableStore_WithChunkFts()
    {
        await using (var seed = new WorkspaceIndexStore(_databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await SeedSearchableChunkAsync(seed);
            // R22: an older extraction-contract stamp (not a product-version bump) is what forces a rebuild now.
            await seed.SetMetaAsync(WorkspaceIndexStore.ExtractionVersionMetaKey, "0", CancellationToken.None);
        }


        await using var reopened = new WorkspaceIndexStore(_databasePath);
        var outcome = await reopened.InitializeAsync(CancellationToken.None);
        Assert.True(outcome.RebuiltEmptyStore);
        Assert.Contains("after upgrade to", outcome.Detail, StringComparison.OrdinalIgnoreCase);

        // The rebuilt store is fully searchable: chunk_fts exists and a freshly indexed chunk is found.
        Assert.True(reopened.FullTextSearchAvailable);
        await SeedSearchableChunkAsync(reopened);
        var hits = await reopened.SearchAsync(new SearchQuery("OrderProcessor", 10), CancellationToken.None);
        Assert.NotEmpty(hits);
    }

    private async Task SeedSearchableChunkAsync(WorkspaceIndexStore store)
    {
        const string path = "src/OrderProcessor.cs";
        await store.UpsertFilesAsync(
        [
            new IndexedFileRecord(path, path, ".cs", 100, 0, "hash-op", Language: "csharp"),
        ], CancellationToken.None);
        await store.UpsertSymbolsAsync(
        [
            new SymbolRecord("sym-op", path, "type", "OrderProcessor", "Shop.OrderProcessor", IsPublicApi: true),
        ], CancellationToken.None);
        await store.UpsertChunksAsync(
        [
            new ChunkRecord(
                "chunk-op", path, "type", "stable-op", 1, 20, "text-hash", 40, 20,
                SymbolId: "sym-op", Name: "OrderProcessor", Signature: "public class OrderProcessor",
                Body: "public class OrderProcessor { }", SymbolsText: "OrderProcessor"),
        ], CancellationToken.None);
    }

    private void DropChunkFts()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE IF EXISTS chunk_fts;";
        command.ExecuteNonQuery();
    }

    private async Task<bool> ChunkFtsExistsAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE name = 'chunk_fts' LIMIT 1;";
        return await command.ExecuteScalarAsync() is not null;
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
        }
    }
}
