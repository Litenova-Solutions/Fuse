using Fuse.Indexing;
using Fuse.Retrieval;
using Fuse.Scoping;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// P6.2: per-changed-file semantic impact (callers, DI consumers, handlers, tests) around changed files.
public sealed class ReviewImpactTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-review-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await SeedAsync();
    }

    [Fact]
    public async Task ChangedFileIsMarkedChangedAndMustKeep()
    {
        var plan = await Review("src/OrderService.cs");

        var changed = Assert.Single(plan.Items, i => i.Path == "src/OrderService.cs");
        Assert.Equal("changed", changed.Role);
        Assert.True(changed.MustKeep);
    }

    [Fact]
    public async Task ImpactIncludesConsumersAndInterface()
    {
        var plan = await Review("src/OrderService.cs");

        var paths = plan.Items.Select(i => i.Path).ToHashSet();
        // OrderService changed -> IOrderService (di_resolves_to incoming) -> OrdersController (di_injects incoming).
        Assert.Contains("src/IOrderService.cs", paths);
        Assert.Contains("src/OrdersController.cs", paths);
    }

    [Fact]
    public async Task NoChangeSourceYieldsWarning()
    {
        var engine = new SemanticRetrievalEngine(_store, changeSource: null);

        var plan = await engine.ReviewAsync(new ReviewRequest(".", "origin/main"), CancellationToken.None);

        Assert.Empty(plan.Items);
        Assert.Contains(plan.Warnings, w => w.Contains("change source", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ContextPlan> Review(params string[] changed)
    {
        var engine = new SemanticRetrievalEngine(_store, new StubChangeSource(changed));
        return await engine.ReviewAsync(new ReviewRequest(".", "origin/main"), CancellationToken.None);
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

    private sealed class StubChangeSource(IReadOnlyList<string> changed) : IChangeSource
    {
        public Task<IReadOnlyList<string>> GetChangedFilesAsync(string rootDirectory, string since, CancellationToken cancellationToken) =>
            Task.FromResult(changed);

        public Task<IReadOnlyList<ChangedFile>> GetDiffsAsync(string rootDirectory, string since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChangedFile>>(changed.Select(p => new ChangedFile(p, 1, 0, string.Empty)).ToList());
    }
}
