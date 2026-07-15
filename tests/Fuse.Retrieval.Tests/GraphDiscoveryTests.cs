using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// S7: graph-aware discovery expands a weak seed through the typed graph, so a query that lexically hits only an
// interface pulls in its implementation even when the implementation shares no vocabulary with the query.
public sealed class GraphDiscoveryTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-discovery-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await SeedFilesAndNodesAsync();
    }

    [Fact]
    public async Task ExpandsFromAnInterfaceToItsImplementationWithNoSharedVocabulary()
    {
        // The implementation "StripeAdapter" shares no words with the query, so it can only arrive via the
        // di_resolves_to edge from the interface the query does hit.
        await _store.UpsertEdgesAsync(
            [new SemanticEdgeRecord("type:IPaymentGateway", "type:StripeAdapter", "di_resolves_to", 0.95, 0.95)],
            CancellationToken.None);
        var engine = new SemanticRetrievalEngine(_store);

        var result = await engine.LocalizeAsync(new LocalizationRequest(".", Query: "PaymentGateway", ExpandGraph: true), CancellationToken.None);

        var paths = result.Candidates.Select(c => c.Path).ToList();
        Assert.Contains("src/IPaymentGateway.cs", paths);
        Assert.Contains("src/StripeAdapter.cs", paths);
    }

    [Fact]
    public async Task DegradesToLexicalWithNoSemanticGraph()
    {
        // No edges (syntax mode): expansion is a no-op, so the unrelated implementation is not pulled in.
        var engine = new SemanticRetrievalEngine(_store);

        var result = await engine.LocalizeAsync(new LocalizationRequest(".", Query: "PaymentGateway", ExpandGraph: true), CancellationToken.None);

        var paths = result.Candidates.Select(c => c.Path).ToList();
        Assert.Contains("src/IPaymentGateway.cs", paths);
        Assert.DoesNotContain("src/StripeAdapter.cs", paths);
    }

    private async Task SeedFilesAndNodesAsync()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/IPaymentGateway.cs", "src/IPaymentGateway.cs", ".cs", 10, 1, "h1"),
                new IndexedFileRecord("src/StripeAdapter.cs", "src/StripeAdapter.cs", ".cs", 10, 1, "h2"),
            ],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:IPaymentGateway", "src/IPaymentGateway.cs", "interface", "k1", 1, 5, "t1", 12, 8, Name: "IPaymentGateway"),
                new ChunkRecord("chunk:StripeAdapter", "src/StripeAdapter.cs", "type", "k2", 1, 5, "t2", 12, 8, Name: "StripeAdapter"),
            ],
            CancellationToken.None);
        await _store.UpsertNodesAsync(
            [
                new NodeRecord("type:IPaymentGateway", "interface", "IPaymentGateway", "App.IPaymentGateway", "src/IPaymentGateway.cs"),
                new NodeRecord("type:StripeAdapter", "class", "StripeAdapter", "App.StripeAdapter", "src/StripeAdapter.cs"),
            ],
            CancellationToken.None);
    }

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
