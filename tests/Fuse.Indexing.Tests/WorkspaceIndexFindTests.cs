using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// P8.1: symbol-by-name lookup backing the find command.
public sealed class WorkspaceIndexFindTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-find-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 20, 1, "h1")],
            CancellationToken.None);
        await _store.UpsertSymbolsAsync(
            [
                new SymbolRecord("symbol:App.OrderService", "src/OrderService.cs", "class", "OrderService", "App.OrderService", IsPublicApi: true, StartLine: 1, EndLine: 20),
                new SymbolRecord("symbol:App.OrderService.Create", "src/OrderService.cs", "method", "Create", "App.OrderService.Create", StartLine: 5, EndLine: 8),
            ],
            CancellationToken.None);
    }

    [Fact]
    public async Task FindsSymbolByNameFragment()
    {
        var symbols = await _store.FindSymbolsByNameAsync("Order", 50, CancellationToken.None);

        Assert.Contains(symbols, s => s.Name == "OrderService" && s.Kind == "class");
    }

    [Fact]
    public async Task PublicApiSymbolsRankFirst()
    {
        var symbols = await _store.FindSymbolsByNameAsync("e", 50, CancellationToken.None);

        Assert.NotEmpty(symbols);
        Assert.True(symbols[0].IsPublicApi, "public-API symbol should rank before non-public ones");
    }

    [Fact]
    public async Task ReturnsEmptyForUnknownName()
    {
        var symbols = await _store.FindSymbolsByNameAsync("zzznope", 50, CancellationToken.None);

        Assert.Empty(symbols);
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
