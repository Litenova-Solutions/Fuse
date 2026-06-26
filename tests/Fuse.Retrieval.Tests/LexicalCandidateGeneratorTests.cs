using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// R1: the lexical candidate generator preserves the BM25F rank (a stronger lexical match scores higher) and
// runs one round of pseudo-relevance feedback to surface vocabulary-related files.
public sealed class LexicalCandidateGeneratorTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-lexical-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RanksAnIdentifierRichMatchAboveAWeakerBodyMatch()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 20, 1, "h1"),
                new IndexedFileRecord("src/Unrelated.cs", "src/Unrelated.cs", ".cs", 20, 1, "h2"),
            ],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:OrderService", "src/OrderService.cs", "type", "k", 1, 20, "th1", 50, 20,
                    Name: "OrderService", Body: "the order service implementation"),
                new ChunkRecord("chunk:Unrelated", "src/Unrelated.cs", "type", "k", 1, 20, "th2", 50, 20,
                    Name: "Unrelated", Body: "the order word appears here once"),
            ],
            CancellationToken.None);

        var candidates = await new LexicalCandidateGenerator(_store)
            .GenerateAsync(new LocalizationRequest(".", Query: "order service"), CancellationToken.None);

        var order = candidates.Single(c => c.FilePath == "src/OrderService.cs");
        var unrelated = candidates.Single(c => c.FilePath == "src/Unrelated.cs");
        Assert.Equal(CandidateSource.FtsSymbol, order.Source);
        Assert.True(order.BaseScore > unrelated.BaseScore,
            $"name match {order.BaseScore} should outrank body match {unrelated.BaseScore}");
    }

    [Fact]
    public async Task PseudoRelevanceFeedbackSurfacesAVocabularyRelatedFile()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/CheckoutHandler.cs", "src/CheckoutHandler.cs", ".cs", 20, 1, "h1"),
                new IndexedFileRecord("src/OrderProcessor.cs", "src/OrderProcessor.cs", ".cs", 20, 1, "h2"),
            ],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                // The query "checkout" matches this file by name; its name supplies the PRF term "Handler".
                new ChunkRecord("chunk:Checkout", "src/CheckoutHandler.cs", "type", "k", 1, 20, "th1", 50, 20,
                    Name: "CheckoutHandler", Body: "begins the checkout"),
                // This file does not contain "checkout"; only the expanded query (with "handler") reaches it.
                new ChunkRecord("chunk:Order", "src/OrderProcessor.cs", "type", "k", 1, 20, "th2", 50, 20,
                    Name: "OrderProcessor", Body: "the handler pipeline runs here"),
            ],
            CancellationToken.None);

        var candidates = await new LexicalCandidateGenerator(_store)
            .GenerateAsync(new LocalizationRequest(".", Query: "checkout"), CancellationToken.None);

        var processor = Assert.Single(candidates, c => c.FilePath == "src/OrderProcessor.cs");
        Assert.Contains(processor.Reasons, r => r.Contains("PRF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyQueryYieldsNoCandidates()
    {
        var candidates = await new LexicalCandidateGenerator(_store)
            .GenerateAsync(new LocalizationRequest("."), CancellationToken.None);

        Assert.Empty(candidates);
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
        }
    }
}
