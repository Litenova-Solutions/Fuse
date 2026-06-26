using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// P5.4: localization results and context-plan output over a seeded semantic index.
public sealed class SemanticRetrievalEngineTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-engine-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await SeedAsync();
    }

    [Fact]
    public async Task LocalizeReturnsRankedCandidatesWithoutBodies()
    {
        var engine = new SemanticRetrievalEngine(_store);

        var result = await engine.LocalizeAsync(new LocalizationRequest(".", Query: "OrderService"), CancellationToken.None);

        Assert.NotEmpty(result.Candidates);
        Assert.Contains(result.Candidates, c => c.Path == "src/OrderService.cs");
        Assert.All(result.Candidates, c => Assert.True(c.Score > 0));
    }

    [Fact]
    public async Task LocalizeWarnsOnLowSignal()
    {
        var engine = new SemanticRetrievalEngine(_store);

        var result = await engine.LocalizeAsync(new LocalizationRequest(".", Query: "zzznotfound"), CancellationToken.None);

        Assert.Empty(result.Candidates);
        Assert.Contains(result.Warnings, w => w.Contains("Low signal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContextPlanIncludesInterfaceImplementationAndConsumer()
    {
        var engine = new SemanticRetrievalEngine(_store);
        var request = new ContextRequest(".", [new ContextSeed(ContextSeedKind.Symbol, "IOrderService")]);

        var plan = await engine.PlanContextAsync(request, CancellationToken.None);

        var paths = plan.Items.Select(i => i.Path).ToHashSet();
        Assert.Contains("src/IOrderService.cs", paths);    // the seed interface
        Assert.Contains("src/OrderService.cs", paths);     // di_resolves_to implementation
        Assert.Contains("src/OrdersController.cs", paths);  // di_injects consumer (incoming)
        Assert.Contains(plan.Items, i => i.Role == "exact-seed" && i.MustKeep);
        Assert.Contains(plan.Items, i => i.Role == "di-implementation");
    }

    [Fact]
    public async Task FileSeedExpandsFromTheFilesSymbols()
    {
        var engine = new SemanticRetrievalEngine(_store);
        // Seeding the file that declares IOrderService should expand to its implementation, like a named seed.
        var request = new ContextRequest(".", [new ContextSeed(ContextSeedKind.File, "src/IOrderService.cs")]);

        var plan = await engine.PlanContextAsync(request, CancellationToken.None);

        var paths = plan.Items.Select(i => i.Path).ToHashSet();
        Assert.Contains("src/IOrderService.cs", paths);
        Assert.Contains("src/OrderService.cs", paths);
    }

    [Fact]
    public async Task ContextPlanRespectsTokenBudget()
    {
        var engine = new SemanticRetrievalEngine(_store);
        var request = new ContextRequest(".", [new ContextSeed(ContextSeedKind.Symbol, "IOrderService")], MaxTokens: 1);

        var plan = await engine.PlanContextAsync(request, CancellationToken.None);

        // Must-keep seed is always included; optional files past the 1-token budget are dropped with a warning.
        Assert.Contains(plan.Items, i => i.MustKeep);
        Assert.Contains(plan.Warnings, w => w.Contains("budget", StringComparison.Ordinal));
    }

    private async Task SeedAsync()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/IOrderService.cs", "src/IOrderService.cs", ".cs", 10, 1, "h1"),
                new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 20, 1, "h2"),
                new IndexedFileRecord("src/OrdersController.cs", "src/OrdersController.cs", ".cs", 30, 1, "h3"),
            ],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:IOrderService", "src/IOrderService.cs", "interface", "k1", 1, 5, "t1", 12, 8, Name: "IOrderService"),
                new ChunkRecord("chunk:OrderService", "src/OrderService.cs", "type", "k2", 1, 20, "t2", 60, 40, Name: "OrderService", Body: "order service"),
                new ChunkRecord("chunk:OrdersController", "src/OrdersController.cs", "type", "k3", 1, 30, "t3", 80, 50, Name: "OrdersController"),
            ],
            CancellationToken.None);
        await _store.UpsertNodesAsync(
            [
                new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "src/IOrderService.cs"),
                new NodeRecord("type:App.OrderService", "class", "OrderService", "App.OrderService", "src/OrderService.cs"),
                new NodeRecord("type:App.OrdersController", "class", "OrdersController", "App.OrdersController", "src/OrdersController.cs"),
            ],
            CancellationToken.None);
        await _store.UpsertEdgesAsync(
            [
                new SemanticEdgeRecord("type:App.IOrderService", "type:App.OrderService", "di_resolves_to", 0.95, 0.95),
                new SemanticEdgeRecord("type:App.OrdersController", "type:App.IOrderService", "di_injects", 0.75, 1.0),
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
