using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// S8: the exploration primitives return ranked, bounded, body-free items with provenance.
public sealed class GraphNeighborhoodExplorerTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-explore-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;
    private GraphNeighborhoodExplorer _explorer = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await SeedAsync();
        _explorer = new GraphNeighborhoodExplorer(_store);
    }

    [Fact]
    public async Task NeighborhoodReturnsImplementerAndCallerWithProvenance()
    {
        var items = await _explorer.NeighborhoodAsync("src/Orders/IOrderService.cs", 20, CancellationToken.None);

        var paths = items.Select(i => i.Path).ToList();
        Assert.Contains("src/Orders/OrderService.cs", paths);     // di_resolves_to (outgoing)
        Assert.Contains("src/Orders/OrdersController.cs", paths);  // di_injects (incoming)
        Assert.DoesNotContain("src/Orders/IOrderService.cs", paths); // never the seed itself
        Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Reason)));
    }

    [Fact]
    public async Task CallersAndImplementersReturnsIncomingDependents()
    {
        var items = await _explorer.CallersAndImplementersAsync("IOrderService", 20, CancellationToken.None);

        Assert.Contains(items, i => i.Path == "src/Orders/OrdersController.cs" && i.Reason.Contains("di_injects", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CentralFilesRanksTheHighestDegreeFileFirst()
    {
        var items = await _explorer.CentralFilesAsync(string.Empty, 10, CancellationToken.None);

        Assert.NotEmpty(items);
        // The interface is depended on (resolves to an impl, injected by a controller): highest degree.
        Assert.Equal("src/Orders/IOrderService.cs", items[0].Path);
        Assert.Contains("degree", items[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NeighborhoodFallsBackToSameFolderWithNoEdges()
    {
        await using var bare = new WorkspaceIndexStore(
            Path.Combine(Path.GetTempPath(), "fuse-explore-bare", Guid.NewGuid().ToString("N"), "fuse.db"));
        await bare.InitializeAsync(CancellationToken.None);
        await bare.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/A/One.cs", "src/A/One.cs", ".cs", 10, 1, "h1"),
                new IndexedFileRecord("src/A/Two.cs", "src/A/Two.cs", ".cs", 10, 1, "h2"),
            ],
            CancellationToken.None);
        var explorer = new GraphNeighborhoodExplorer(bare);

        var items = await explorer.NeighborhoodAsync("src/A/One.cs", 20, CancellationToken.None);

        // No edges: the neighborhood is same-folder cohesion, never the seed itself.
        Assert.Contains(items, i => i.Path == "src/A/Two.cs" && i.Reason == "same folder");
        Assert.DoesNotContain(items, i => i.Path == "src/A/One.cs");
    }

    [Fact]
    public async Task CoveringTestsSelectsOnlyTheTestsEdgeSource()
    {
        // A change to OrderService is covered by the test that carries a tests edge to it; a caller (the
        // controller, reached via di_injects) is blast radius, not a covering test, and must not be selected.
        var covering = await _explorer.CoveringTestsAsync("OrderService", 20, CancellationToken.None);

        Assert.Contains(covering, i => i.Path == "tests/OrderServiceTests.cs");
        Assert.DoesNotContain(covering, i => i.Path == "src/Orders/OrdersController.cs");
        Assert.All(covering, i => Assert.Equal("covers", i.Reason));
    }

    [Fact]
    public async Task CoveringTestsIsEmptyWhenNoTestsEdgeReachesTheSymbol()
        => Assert.Empty(await _explorer.CoveringTestsAsync("IOrderService", 20, CancellationToken.None));

    private async Task SeedAsync()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/Orders/IOrderService.cs", "src/Orders/IOrderService.cs", ".cs", 10, 1, "h1"),
                new IndexedFileRecord("src/Orders/OrderService.cs", "src/Orders/OrderService.cs", ".cs", 10, 1, "h2"),
                new IndexedFileRecord("src/Orders/OrdersController.cs", "src/Orders/OrdersController.cs", ".cs", 10, 1, "h3"),
                new IndexedFileRecord("tests/OrderServiceTests.cs", "tests/OrderServiceTests.cs", ".cs", 10, 1, "h4"),
            ],
            CancellationToken.None);
        await _store.UpsertNodesAsync(
            [
                new NodeRecord("type:IOrderService", "interface", "IOrderService", "App.IOrderService", "src/Orders/IOrderService.cs"),
                new NodeRecord("type:OrderService", "class", "OrderService", "App.OrderService", "src/Orders/OrderService.cs"),
                new NodeRecord("type:OrdersController", "class", "OrdersController", "App.OrdersController", "src/Orders/OrdersController.cs"),
                new NodeRecord("type:OrderServiceTests", "class", "OrderServiceTests", "App.Tests.OrderServiceTests", "tests/OrderServiceTests.cs"),
            ],
            CancellationToken.None);
        await _store.UpsertEdgesAsync(
            [
                new SemanticEdgeRecord("type:IOrderService", "type:OrderService", "di_resolves_to", 0.95, 0.95),
                new SemanticEdgeRecord("type:OrdersController", "type:IOrderService", "di_injects", 0.75, 1.0),
                // R5 DI-resolved tests edge: the test injects IOrderService, resolved to OrderService.
                new SemanticEdgeRecord("type:OrderServiceTests", "type:OrderService", "tests", 0.9, 1.0),
            ],
            CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
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
