using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// The store side of fuse_find (kind=signatures). GetSignaturesByNamesAsync returns exact signatures for a batch of names,
// matched by simple name or fully qualified name, public-API first, and returns nothing for an unknown name.
public sealed class WorkspaceIndexSignatureTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-sig-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 120, 1, "h1")],
            CancellationToken.None);
        await _store.UpsertSymbolsAsync(
            [
                new SymbolRecord("symbol:App.OrderService.Create", "src/OrderService.cs", "method", "Create",
                    "App.OrderService.Create", ContainingType: "App.OrderService", Accessibility: "public",
                    Signature: "public Order Create(CreateOrderCommand command)", StartLine: 10, EndLine: 14, IsPublicApi: true),
                new SymbolRecord("symbol:App.OrderService", "src/OrderService.cs", "type", "OrderService",
                    "App.OrderService", Accessibility: "public", Signature: "public sealed class OrderService", StartLine: 1, EndLine: 40, IsPublicApi: true),
            ],
            CancellationToken.None);
    }

    [Fact]
    public async Task GetSignatures_matches_simple_name_and_returns_the_signature()
    {
        var results = await _store.GetSignaturesByNamesAsync(["Create"], 5, CancellationToken.None);
        var match = Assert.Single(results);
        Assert.Equal("Create", match.Name);
        Assert.Equal("public Order Create(CreateOrderCommand command)", match.Signature);
        Assert.Equal("App.OrderService", match.ContainingType);
        Assert.True(match.IsPublicApi);
    }

    [Fact]
    public async Task GetSignatures_matches_fully_qualified_name()
    {
        var results = await _store.GetSignaturesByNamesAsync(["App.OrderService"], 5, CancellationToken.None);
        var match = Assert.Single(results);
        Assert.Equal("public sealed class OrderService", match.Signature);
    }

    [Fact]
    public async Task GetSignatures_returns_empty_for_an_unknown_name()
        => Assert.Empty(await _store.GetSignaturesByNamesAsync(["Nonexistent"], 5, CancellationToken.None));

    [Fact]
    public async Task GetMembersOfType_returns_the_types_members_by_simple_or_qualified_name()
    {
        // R6 repair packet: the member Create belongs to App.OrderService, matched by simple or full type name.
        var bySimple = await _store.GetMembersOfTypeAsync("OrderService", 10, CancellationToken.None);
        Assert.Contains(bySimple, m => m.Name == "Create");

        var byFull = await _store.GetMembersOfTypeAsync("App.OrderService", 10, CancellationToken.None);
        Assert.Contains(byFull, m => m.Name == "Create");
    }

    [Fact]
    public async Task GetMembersOfType_is_empty_for_an_unknown_type()
        => Assert.Empty(await _store.GetMembersOfTypeAsync("NoSuchType", 10, CancellationToken.None));

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        var directory = Path.GetDirectoryName(_databasePath);
        SqliteConnection.ClearAllPools();
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
