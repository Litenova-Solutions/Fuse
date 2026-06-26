using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Scoping;

namespace Fuse.Retrieval;

/// <summary>
///     Generates candidates from the persistent dense embedding index: the query is embedded and ranked by
///     cosine similarity against the per-chunk vectors, so a prose query can surface a file it shares no tokens
///     with. This is the channel that attacks the natural-language bucket where lexical matching fails.
/// </summary>
/// <remarks>
///     Optional and model-gated: when no embedder is available (no model present, offline) or the index holds no
///     embeddings, the generator returns nothing and retrieval stays lexical, preserving the no-model floor.
///     The workspace's embeddings are loaded once and cached on the instance, so repeated queries against the
///     same index (the warm path) do not re-read the store. Vectors are unit length, so cosine is a dot product.
/// </remarks>
public sealed class DenseCandidateGenerator : ICandidateGenerator
{
    // The number of top files emitted as candidates; the lexical generators contribute the rest of the pool.
    private const int TopFiles = 20;

    // The weakest emitted file keeps this fraction of the dense band ceiling, so a real semantic match is not
    // zeroed by min-max normalization over the pool.
    private const double SimilarityFloor = 0.4;

    private readonly IWorkspaceIndexStore _store;
    private readonly ITextEmbedder? _embedder;
    private IReadOnlyList<ChunkEmbedding>? _cached;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DenseCandidateGenerator" /> class.
    /// </summary>
    /// <param name="store">The index store holding the persisted embeddings.</param>
    /// <param name="embedder">The text embedder, or null when no dense model is available.</param>
    public DenseCandidateGenerator(IWorkspaceIndexStore store, ITextEmbedder? embedder)
    {
        _store = store;
        _embedder = embedder;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CandidateNode>> GenerateAsync(LocalizationRequest request, CancellationToken cancellationToken)
    {
        if (_embedder is null || !_embedder.IsAvailable || string.IsNullOrWhiteSpace(request.Query))
            return [];

        var queryVector = _embedder.Embed(request.Query);
        if (queryVector.Length == 0)
            return [];

        var embeddings = _cached ??= await _store.GetEmbeddingsAsync(cancellationToken);
        if (embeddings.Count == 0)
            return [];

        // Collapse chunk vectors to the best (max) cosine per file, then keep the top files.
        var bestByFile = new Dictionary<string, (double Score, string? Name)>(StringComparer.Ordinal);
        foreach (var embedding in embeddings)
        {
            var score = Dot(queryVector, embedding.Vector);
            if (!bestByFile.TryGetValue(embedding.FilePath, out var current) || score > current.Score)
                bestByFile[embedding.FilePath] = (score, embedding.Name);
        }

        var top = bestByFile
            .OrderByDescending(kv => kv.Value.Score)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(Math.Max(TopFiles, request.MaxCandidates))
            .ToList();
        if (top.Count == 0)
            return [];

        // Min-max normalize the kept cosines to [SimilarityFloor, 1] so the dense rank survives the noisy-or
        // merge without the raw cosine scale dominating the other generators.
        var max = top[0].Value.Score;
        var min = top[^1].Value.Score;
        var range = max - min;
        var ceiling = CandidateSourceWeights.Weight(CandidateSource.Dense);

        var candidates = new List<CandidateNode>(top.Count);
        foreach (var (path, (score, name)) in top)
        {
            var normalized = range <= 0 ? 1.0 : SimilarityFloor + (1 - SimilarityFloor) * ((score - min) / range);
            candidates.Add(new CandidateNode(
                NodeId: string.Empty,
                FilePath: path,
                Kind: "file",
                BaseScore: ceiling * normalized,
                Source: CandidateSource.Dense,
                Reasons: [$"dense match: {name ?? "chunk"} (cosine {score:F2})"],
                TokenEstimate: 0));
        }

        return candidates;
    }

    private static double Dot(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length)
            return 0.0;

        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
            sum += a[i] * (double)b[i];
        return sum;
    }
}
