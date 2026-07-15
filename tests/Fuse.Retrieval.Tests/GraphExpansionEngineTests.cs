using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// P5.3: weighted graph expansion with typed edges, decay, and pruning.
public sealed class GraphExpansionEngineTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-expand-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await SeedAsync();
    }

    [Fact]
    public async Task ExpandsFromSeedAcrossEdges()
    {
        var expanded = await Expand("type:App.OrdersController", depth: 2, threshold: 0.05);

        var ids = expanded.Select(e => e.NodeId).ToHashSet();
        // Controller -> IOrderService (di_injects), -> OrderService (di_depends_on_impl), then IOrderService ->
        // OrderService (di_resolves_to) is reachable within depth 2.
        Assert.Contains("type:App.OrdersController", ids);
        Assert.Contains("type:App.IOrderService", ids);
        Assert.Contains("type:App.OrderService", ids);
    }

    [Fact]
    public async Task SeedIsMustKeepAtHopZero()
    {
        var expanded = await Expand("type:App.OrdersController", depth: 2, threshold: 0.05);

        var seed = expanded.Single(e => e.NodeId == "type:App.OrdersController");
        Assert.True(seed.MustKeep);
        Assert.Equal(0, seed.Hop);
    }

    [Fact]
    public async Task DepthZeroDoesNotExpand()
    {
        var expanded = await Expand("type:App.OrdersController", depth: 0, threshold: 0.05);

        Assert.Single(expanded);
        Assert.Equal("type:App.OrdersController", expanded[0].NodeId);
    }

    [Fact]
    public async Task HighThresholdPrunesDistantNodes()
    {
        var expanded = await Expand("type:App.OrdersController", depth: 3, threshold: 0.95);

        // Only the seed survives a threshold above any decayed child score.
        Assert.Single(expanded);
    }

    private async Task<IReadOnlyList<ExpandedNode>> Expand(string seedNodeId, int depth, double threshold)
    {
        var engine = new GraphExpansionEngine(_store, new EdgeWeightProvider());
        var seed = new ScoredCandidate(seedNodeId, "src/OrdersController.cs", "class", 1.0, [], ["seed"], 0);
        return await engine.ExpandAsync([seed], depth, threshold, CancellationToken.None);
    }

    private async Task SeedAsync()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/OrdersController.cs", "src/OrdersController.cs", ".cs", 30, 1, "h1"),
                new IndexedFileRecord("src/IOrderService.cs", "src/IOrderService.cs", ".cs", 10, 1, "h2"),
                new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 20, 1, "h3"),
            ],
            CancellationToken.None);
        await _store.UpsertNodesAsync(
            [
                new NodeRecord("type:App.OrdersController", "class", "OrdersController", "App.OrdersController", "src/OrdersController.cs"),
                new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "src/IOrderService.cs"),
                new NodeRecord("type:App.OrderService", "class", "OrderService", "App.OrderService", "src/OrderService.cs"),
            ],
            CancellationToken.None);
        await _store.UpsertEdgesAsync(
            [
                new SemanticEdgeRecord("type:App.OrdersController", "type:App.IOrderService", "di_injects", 0.75, 1.0),
                new SemanticEdgeRecord("type:App.OrdersController", "type:App.OrderService", "di_depends_on_impl", 0.85, 0.9),
                new SemanticEdgeRecord("type:App.IOrderService", "type:App.OrderService", "di_resolves_to", 0.95, 0.95),
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
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
