using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Retrieval;

/// <summary>
///     A candidate file for reranking: its BM25 result, the text to embed, and an optional cache key.
/// </summary>
/// <param name="Ranked">The BM25 result (path and score).</param>
/// <param name="Text">The text to embed for the candidate, typically its symbols, path, and body.</param>
/// <param name="CacheKey">An optional content-and-model key for the on-disk vector store, or null to skip caching.</param>
public sealed record RerankCandidate(RankedFile Ranked, string Text, string? CacheKey);

/// <summary>
///     Reranks BM25 candidates by blending the normalized BM25 score with the cosine similarity between the
///     query embedding and each candidate embedding.
/// </summary>
/// <remarks>
///     Hybrid retrieval keeps BM25 as the recall stage (it selects the candidates) and uses vectors only to
///     reorder them, so a weak embedding cannot drop a lexically strong match below the cut. The blend weight
///     favors BM25 by default. Vectors are read from and written to the optional store, keyed by content hash,
///     to satisfy the on-disk-vectors design and amortize embedding across a session.
/// </remarks>
public static class VectorReranker
{
    /// <summary>
    ///     Reranks the candidates and returns them in descending blended-score order.
    /// </summary>
    /// <param name="query">The query text.</param>
    /// <param name="candidates">The BM25 candidates to rerank.</param>
    /// <param name="model">The embedding model.</param>
    /// <param name="store">An optional on-disk vector store for caching candidate embeddings.</param>
    /// <param name="bm25Weight">The weight given to the normalized BM25 score; the remainder goes to cosine.</param>
    /// <returns>The reranked candidates as <see cref="RankedFile" /> results with blended scores.</returns>
    public static IReadOnlyList<RankedFile> Rerank(
        string query,
        IReadOnlyList<RerankCandidate> candidates,
        IEmbeddingModel model,
        IVectorStore? store = null,
        double bm25Weight = 0.6)
    {
        if (candidates.Count == 0)
            return [];

        var queryVector = model.Embed(query);

        var maxBm25 = candidates.Max(c => c.Ranked.Score);
        if (maxBm25 <= 0)
            maxBm25 = 1;

        var reranked = new List<RankedFile>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var vector = Embed(candidate, model, store);
            var cosine = Math.Max(0, Dot(queryVector, vector));
            var normalizedBm25 = candidate.Ranked.Score / maxBm25;
            var blended = (bm25Weight * normalizedBm25) + ((1 - bm25Weight) * cosine);
            reranked.Add(new RankedFile(candidate.Ranked.Path, blended));
        }

        return reranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static float[] Embed(RerankCandidate candidate, IEmbeddingModel model, IVectorStore? store)
    {
        if (candidate.CacheKey is not null && store is not null && store.TryGet(candidate.CacheKey, out var cached) && cached is not null)
            return cached;

        var vector = model.Embed(candidate.Text);
        if (candidate.CacheKey is not null && store is not null)
            store.Set(candidate.CacheKey, vector);

        return vector;
    }

    private static double Dot(float[] a, float[] b)
    {
        var length = Math.Min(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < length; i++)
            sum += a[i] * (double)b[i];

        return sum;
    }
}
