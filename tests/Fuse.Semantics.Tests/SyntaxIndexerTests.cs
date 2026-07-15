using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Semantics.Tests;

// P2.2: syntax-level symbol + chunk extraction into the store, and FTS over the stored chunks.
// Routed through SemanticIndexer.IndexSyntaxFirstAsync (provider-driven syntax tier).
public sealed class SyntaxIndexerTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "fuse-semantics-tests", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private WorkspaceIndexStore _store = null!;

    public SyntaxIndexerTests() =>
        _databasePath = Path.Combine(_root, ".fuse", "fuse.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "OrderService.cs"), """
            namespace App.Services;

            public interface IOrderService
            {
                void Place(int id);
            }

            public class OrderService : IOrderService
            {
                private readonly int _max;

                public OrderService(int max) => _max = max;

                public void Place(int id) { }
            }
            """);

        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task IndexExtractsTypeAndMemberSymbols()
    {
        var indexer = CreateIndexer();

        var result = await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        Assert.Equal(1, result.FileCount);
        Assert.Equal("syntax", result.Mode);
        // 2 types (IOrderService, OrderService) + members (Place x2, ctor, field, _max field).
        Assert.True(result.SymbolCount >= 5, $"expected >= 5 symbols, got {result.SymbolCount}");
        Assert.True(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'OrderService' AND kind = 'class';"));
        Assert.True(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'IOrderService' AND kind = 'interface';"));
        Assert.True(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'Place' AND kind = 'method';"));
    }

    [Fact]
    public async Task IndexedChunksAreSearchableByFts()
    {
        var indexer = CreateIndexer();
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        var hits = await _store.SearchAsync(new SearchQuery("OrderService"), CancellationToken.None);

        Assert.Contains(hits, h => h.FilePath.EndsWith("src/OrderService.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReindexAfterEditReplacesSymbols()
    {
        var indexer = CreateIndexer();
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);
        var before = await CountAsync("SELECT count(*) FROM symbols;");

        // Edit the file to remove a member, then clear its data and reindex.
        File.WriteAllText(Path.Combine(_root, "src", "OrderService.cs"),
            "namespace App.Services; public class OrderService { }");
        await _store.DeleteFileDataAsync("src/OrderService.cs", CancellationToken.None);
        await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        var after = await CountAsync("SELECT count(*) FROM symbols;");
        Assert.True(after < before, $"expected fewer symbols after trimming the file ({after} < {before})");
        Assert.True(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'OrderService';"));
        Assert.False(await ExistsAsync("SELECT 1 FROM symbols WHERE name = 'IOrderService';"));
    }

    [Fact]
    public async Task IndexStoresRoutesFromControllers()
    {
        File.WriteAllText(Path.Combine(_root, "src", "OrdersController.cs"), """
            using Microsoft.AspNetCore.Mvc;

            [Route("api/orders")]
            public class OrdersController : ControllerBase
            {
                [HttpGet]
                public IActionResult List() => Ok();
            }
            """);
        var indexer = CreateIndexer();

        var result = await indexer.IndexSyntaxFirstAsync(_root, _store, CancellationToken.None);

        Assert.True(result.RouteCount >= 1);
        // A verb attribute with no route argument falls back to the handler name as the path segment.
        Assert.True(await ExistsAsync("SELECT 1 FROM routes WHERE route_pattern = '/api/orders/List' AND http_method = 'GET';"));
    }

    private static SemanticIndexer CreateIndexer()
    {
        var fileSystem = new PhysicalFileSystem();
        var pipeline = new FileCollectionPipeline(
            fileSystem,
            new GitIgnoreParser(fileSystem),
            [
                new GitIgnoreFilter(),
                new ExtensionFilter(),
                new ExcludedDirectoryFilter(),
                new EmptyFileFilter(),
                new BinaryFileFilter(fileSystem),
            ]);
        return new SemanticIndexer(
            new DotNetWorkspaceDiscoverer(),
            new RoslynWorkspaceLoader(),
            new WorkspaceFileScanner(pipeline, new FileHashService()),
            new SemanticSymbolExtractor(),
            new SyntaxSymbolExtractor(),
            new SyntaxRouteExtractor(),
            new FileHashService(),
            Fuse.Semantics.Analyzers.SemanticAnalysisRunner.CreateDefault());
    }

    private async Task<bool> ExistsAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None) is not null;
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None) is long value ? value : 0;
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
