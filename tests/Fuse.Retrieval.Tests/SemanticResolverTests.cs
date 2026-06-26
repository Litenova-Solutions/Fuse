using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// P4.8: the resolver answers service/request/route/config queries against a seeded semantic index.
public sealed class SemanticResolverTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-resolver-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await SeedAsync();
    }

    [Fact]
    public async Task ResolvesServiceToImplementation()
    {
        var resolver = new SemanticResolver(_store);

        var result = await resolver.ResolveServiceAsync("IOrderService", CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal("OrderService", match.DisplayName);
        Assert.Equal("di_resolves_to", match.Relation);
        Assert.Equal("src/OrderService.cs", match.FilePath);
    }

    [Fact]
    public async Task ResolvesRequestToHandler()
    {
        var resolver = new SemanticResolver(_store);

        var result = await resolver.ResolveRequestAsync("CreateOrderCommand", CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal("CreateOrderHandler", match.DisplayName);
        Assert.Equal("mediatr_handles", match.Relation);
    }

    [Fact]
    public async Task ResolvesRouteToActionMethod()
    {
        var resolver = new SemanticResolver(_store);

        var result = await resolver.ResolveRouteAsync("POST /api/orders/{id}", CancellationToken.None);

        var match = Assert.Single(result.Matches);
        Assert.Equal("Create", match.DisplayName);
        Assert.Equal("route_handles", match.Relation);
    }

    [Fact]
    public async Task ReturnsNoMatchesForUnknownService()
    {
        var resolver = new SemanticResolver(_store);

        var result = await resolver.ResolveServiceAsync("IDoesNotExist", CancellationToken.None);

        Assert.Empty(result.Matches);
    }

    private async Task SeedAsync()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/IOrderService.cs", "src/IOrderService.cs", ".cs", 10, 1, "h1"),
                new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 20, 1, "h2"),
                new IndexedFileRecord("src/CreateOrderCommand.cs", "src/CreateOrderCommand.cs", ".cs", 10, 1, "h3"),
                new IndexedFileRecord("src/CreateOrderHandler.cs", "src/CreateOrderHandler.cs", ".cs", 20, 1, "h4"),
                new IndexedFileRecord("src/OrdersController.cs", "src/OrdersController.cs", ".cs", 30, 1, "h5"),
            ],
            CancellationToken.None);

        await _store.UpsertNodesAsync(
            [
                new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "src/IOrderService.cs"),
                new NodeRecord("type:App.OrderService", "class", "OrderService", "App.OrderService", "src/OrderService.cs"),
                new NodeRecord("type:App.CreateOrderCommand", "record", "CreateOrderCommand", "App.CreateOrderCommand", "src/CreateOrderCommand.cs"),
                new NodeRecord("type:App.CreateOrderHandler", "class", "CreateOrderHandler", "App.CreateOrderHandler", "src/CreateOrderHandler.cs"),
                new NodeRecord("route:POST:/api/orders/{id}", "route", "POST /api/orders/{id}", "route:POST:/api/orders/{id}", "src/OrdersController.cs"),
                new NodeRecord("method:App.OrdersController.Create", "method", "Create", "App.OrdersController.Create", "src/OrdersController.cs"),
            ],
            CancellationToken.None);

        await _store.UpsertEdgesAsync(
            [
                new SemanticEdgeRecord("type:App.IOrderService", "type:App.OrderService", "di_resolves_to", 0.95, 0.95),
                new SemanticEdgeRecord("type:App.CreateOrderCommand", "type:App.CreateOrderHandler", "mediatr_handles", 0.95, 0.95),
                new SemanticEdgeRecord("route:POST:/api/orders/{id}", "method:App.OrdersController.Create", "route_handles", 1.0, 1.0),
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
