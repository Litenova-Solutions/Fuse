using Fuse.Fusion.Retrieval;
using Fuse.Fusion.Scoping;
using Fuse.Reduction.Caching;

namespace Fuse.Fusion.Tests.Retrieval;

public class HashingEmbeddingModelTests
{
    private readonly HashingEmbeddingModel _model = new(64);

    [Fact]
    public void Embed_IsDeterministic()
    {
        Assert.Equal(_model.Embed("order service charge"), _model.Embed("order service charge"));
    }

    [Fact]
    public void Embed_EmptyText_IsZeroVector()
    {
        Assert.All(_model.Embed(string.Empty), v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Embed_SimilarTextScoresHigherThanUnrelated()
    {
        var query = _model.Embed("order service charge");
        var related = _model.Embed("OrderService Charge PlaceOrder");
        var unrelated = _model.Embed("xml yaml serializer config");

        Assert.True(Dot(query, related) > Dot(query, unrelated));
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * (double)b[i];
        return sum;
    }
}

public class VectorRerankerTests
{
    private readonly HashingEmbeddingModel _model = new(128);

    [Fact]
    public void Rerank_EmptyCandidates_ReturnsEmpty()
    {
        Assert.Empty(VectorReranker.Rerank("q", [], _model));
    }

    [Fact]
    public void Rerank_PromotesSemanticallyCloserCandidateOnBm25Tie()
    {
        // Two candidates with the same BM25 score; the one whose text is closer to the query should win.
        var candidates = new[]
        {
            new RerankCandidate(new RankedFile("Unrelated.cs", 1.0), "xml yaml serializer settings", null),
            new RerankCandidate(new RankedFile("OrderService.cs", 1.0), "OrderService Charge order payment", null),
        };

        var reranked = VectorReranker.Rerank("order charge payment", candidates, _model);

        Assert.Equal("OrderService.cs", reranked[0].Path);
    }

    [Fact]
    public void Rerank_KeepsOnlyProvidedCandidates()
    {
        var candidates = new[]
        {
            new RerankCandidate(new RankedFile("A.cs", 2.0), "alpha", null),
            new RerankCandidate(new RankedFile("B.cs", 1.0), "beta", null),
        };

        var reranked = VectorReranker.Rerank("alpha", candidates, _model);

        Assert.Equal(2, reranked.Count);
        Assert.Contains(reranked, r => r.Path == "A.cs");
        Assert.Contains(reranked, r => r.Path == "B.cs");
    }
}

public sealed class SqliteVectorStoreTests : IDisposable
{
    private readonly string _databasePath;

    public SqliteVectorStoreTests() =>
        _databasePath = SqliteTestHelpers.NewDatabasePath("fuse-vec-tests");

    [Fact]
    public async Task Set_Then_Get_RoundTripsAcrossInstances()
    {
        var vector = new[] { 0.1f, -0.2f, 0.3f, 0.4f };
        await using (var writerStore = new SqliteKeyValueStore(_databasePath))
        {
            new SqliteVectorStore(writerStore, vector.Length).Set("k1", vector);
            await writerStore.FlushAsync();
        }

        await using var readerStore = new SqliteKeyValueStore(_databasePath);
        var reader = new SqliteVectorStore(readerStore, vector.Length);
        Assert.True(reader.TryGet("k1", out var loaded));
        Assert.Equal(vector, loaded);
    }

    [Fact]
    public async Task TryGet_WrongDimensions_IsMiss()
    {
        await using (var writerStore = new SqliteKeyValueStore(_databasePath))
        {
            new SqliteVectorStore(writerStore, 4).Set("k2", [1f, 2f, 3f, 4f]);
            await writerStore.FlushAsync();
        }

        await using var readerStore = new SqliteKeyValueStore(_databasePath);
        Assert.False(new SqliteVectorStore(readerStore, 8).TryGet("k2", out _));
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_databasePath);
        if (root is not null && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
