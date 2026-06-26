using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Scoping;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// R2: the dense candidate generator ranks by embedding similarity over the persisted vector index, and is a
// no-op (no candidates) when no embedder is available, preserving the no-model floor.
public sealed class DenseCandidateGeneratorTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-dense-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await SeedAsync();
    }

    [Fact]
    public async Task RanksTheSemanticallyNearestFileFirst()
    {
        // The query embeds to [1, 0]; the "billing" chunk's stored vector is [1, 0] (cosine 1), the "shipping"
        // chunk's is [0, 1] (cosine 0), so billing must rank above shipping.
        var embedder = new FakeEmbedder(query: [1f, 0f]);
        var candidates = await new DenseCandidateGenerator(_store, embedder)
            .GenerateAsync(new LocalizationRequest(".", Query: "invoice total"), CancellationToken.None);

        var billing = candidates.Single(c => c.FilePath == "src/Billing.cs");
        var shipping = candidates.Single(c => c.FilePath == "src/Shipping.cs");
        Assert.Equal(CandidateSource.Dense, billing.Source);
        Assert.True(billing.BaseScore > shipping.BaseScore,
            $"billing {billing.BaseScore} should outrank shipping {shipping.BaseScore}");
    }

    [Fact]
    public async Task NoEmbedderYieldsNoCandidates()
    {
        var candidates = await new DenseCandidateGenerator(_store, embedder: null)
            .GenerateAsync(new LocalizationRequest(".", Query: "invoice total"), CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task UnavailableEmbedderYieldsNoCandidates()
    {
        var candidates = await new DenseCandidateGenerator(_store, new FakeEmbedder(query: [1f, 0f], available: false))
            .GenerateAsync(new LocalizationRequest(".", Query: "invoice total"), CancellationToken.None);

        Assert.Empty(candidates);
    }

    private async Task SeedAsync()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/Billing.cs", "src/Billing.cs", ".cs", 10, 1, "h1"),
                new IndexedFileRecord("src/Shipping.cs", "src/Shipping.cs", ".cs", 10, 1, "h2"),
            ],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:Billing", "src/Billing.cs", "type", "k", 1, 10, "t1", 50, 20, Name: "Billing"),
                new ChunkRecord("chunk:Shipping", "src/Shipping.cs", "type", "k", 1, 10, "t2", 50, 20, Name: "Shipping"),
            ],
            CancellationToken.None);
        await _store.UpsertEmbeddingsAsync(
            [
                new ChunkEmbeddingRecord("chunk:Billing", 2, [1f, 0f]),
                new ChunkEmbeddingRecord("chunk:Shipping", 2, [0f, 1f]),
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
        }
    }

    // A deterministic embedder: the query always embeds to the given vector; documents are not embedded here
    // (the generator reads stored chunk vectors), so Embed is only exercised for the query.
    private sealed class FakeEmbedder(float[] query, bool available = true) : ITextEmbedder
    {
        public bool IsAvailable => available;
        public int Dimension => query.Length;
        public float[] Embed(string text) => query;
    }
}
