using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

public sealed class FtsSearchEngineIntegrationTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-fts-port-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexConnectionFactory _factory = null!;
    private FtsSearchEngine _fts = null!;
    private SymbolGraphStore _graph = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _factory = new WorkspaceIndexConnectionFactory(_databasePath);
        _fts = new FtsSearchEngine(_factory);
        _graph = new SymbolGraphStore(_factory, _fts);

        await using var connection = await _factory.OpenAsync(CancellationToken.None);
        await new IndexSchemaMigrator(_factory).PrepareDatabaseAsync(connection, CancellationToken.None);
        await IndexSchemaMigrator.MigrateAsync(connection, CancellationToken.None);
        await IndexSchemaMigrator.EnsureTablesAsync(connection, CancellationToken.None);
        Assert.True(await _fts.TryCreateAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_finds_indexed_chunk_by_name()
    {
        await _graph.UpsertFilesAsync(
            [new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 100, 1, "h1")],
            CancellationToken.None);
        await _graph.UpsertChunksAsync(
            [new ChunkRecord("chunk:1", "src/OrderService.cs", "type", "k1", 1, 10, "th1", 20, 10, Name: "OrderService", Body: "order logic")],
            CancellationToken.None);

        var hits = await _fts.SearchAsync(new SearchQuery("OrderService"), CancellationToken.None);

        Assert.Contains(hits, h => h.Name == "OrderService" && h.FilePath == "src/OrderService.cs");
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        return Task.CompletedTask;
    }
}
