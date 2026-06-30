using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;
using Fuse.Plugins.Abstractions.Scoping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fuse.Plugins.Rerank.Onnx;

/// <summary>
///     An <see cref="IReranker" /> that reorders the BM25 candidate pool with an in-process all-MiniLM-L6-v2
///     sentence embedder, blending the lexical score with dense query-to-document similarity so a file whose
///     meaning matches the query can outrank one that merely shares more words.
/// </summary>
/// <remarks>
///     The model loads lazily on the first rerank and is then reused across calls (ONNX Runtime sessions are
///     safe for concurrent inference). When the model is absent or fails to load the reranker reports
///     unavailable and callers keep the lexical ordering, so retrieval stays on the lexical path when no model is present. Both the
///     lexical and dense signals are min-max normalized over the pool before blending, so the blend is free of
///     each signal's raw scale.
/// </remarks>
public sealed class OnnxDenseReranker : IReranker, IDisposable
{
    // Weight on the lexical (BM25) signal in the blend; the remainder is the dense cosine signal. An even blend
    // keeps a strong exact-term match competitive while letting the embedding break ties and rescue a
    // vocabulary-mismatched file.
    private const double LexicalWeight = 0.5;

    // Above this many cached document embeddings the cache is cleared, bounding memory across a long session.
    // 384 floats per entry is about 1.5 KB, so the cap is a few tens of MB.
    private const int MaxCachedEmbeddings = 20_000;

    private readonly string _onnxModelPath;
    private readonly string _vocabPath;
    private readonly ILogger<OnnxDenseReranker> _logger;
    private readonly object _loadGate = new();

    // Document embeddings cached by content hash (item 23): a candidate's text embeds to the same vector every
    // time, so a warm rerank over an unchanged file skips re-running the model. The query is not cached (it
    // changes per call). Concurrency-safe; bounded by clearing when it grows past the cap.
    private readonly ConcurrentDictionary<ulong, float[]> _embeddingCache = new();

    private MiniLmEmbedder? _embedder;
    private bool _loadFailed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OnnxDenseReranker" /> class over the cached model files.
    /// </summary>
    /// <param name="onnxModelPath">Path to the quantized ONNX graph.</param>
    /// <param name="vocabPath">Path to the WordPiece vocabulary.</param>
    /// <param name="logger">Optional logger; a load failure is logged once and then the reranker stays unavailable.</param>
    public OnnxDenseReranker(string onnxModelPath, string vocabPath, ILogger<OnnxDenseReranker>? logger = null)
    {
        _onnxModelPath = onnxModelPath;
        _vocabPath = vocabPath;
        _logger = logger ?? NullLogger<OnnxDenseReranker>.Instance;
    }

    /// <inheritdoc />
    public bool IsAvailable => !_loadFailed && (_embedder is not null || EnsureLoaded() is not null);

    /// <inheritdoc />
    public IReadOnlyList<RankedFile> Rerank(
        string query,
        IReadOnlyList<RankedFile> candidates,
        IReadOnlyDictionary<string, string> documentText)
    {
        if (candidates.Count < 2)
            return candidates;

        var embedder = EnsureLoaded();
        if (embedder is null)
            return candidates;

        float[] queryVector;
        try
        {
            queryVector = embedder.Embed(query);
        }
        catch (Exception ex)
        {
            // A runtime inference failure must not break scoping: fall back to the lexical order.
            _logger.LogWarning(ex, "Dense rerank query embedding failed; keeping lexical order.");
            return candidates;
        }

        if (queryVector.Length == 0)
            return candidates;

        var cosine = new double[candidates.Count];
        var lexical = new double[candidates.Count];
        try
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                lexical[i] = candidates[i].Score;
                var text = documentText.TryGetValue(candidates[i].Path, out var t) ? t : string.Empty;
                var vector = EmbedDocumentCached(embedder, text);
                cosine[i] = Dot(queryVector, vector);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dense rerank document embedding failed; keeping lexical order.");
            return candidates;
        }

        MinMaxNormalize(lexical);
        MinMaxNormalize(cosine);

        var reranked = new List<RankedFile>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var blended = LexicalWeight * lexical[i] + (1 - LexicalWeight) * cosine[i];
            reranked.Add(candidates[i] with { Score = blended });
        }

        return reranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private MiniLmEmbedder? EnsureLoaded()
    {
        if (_embedder is not null)
            return _embedder;
        if (_loadFailed)
            return null;

        lock (_loadGate)
        {
            if (_embedder is not null)
                return _embedder;
            if (_loadFailed)
                return null;

            if (!File.Exists(_onnxModelPath) || !File.Exists(_vocabPath))
            {
                _loadFailed = true;
                return null;
            }

            try
            {
                _embedder = MiniLmEmbedder.Load(_onnxModelPath, _vocabPath);
                return _embedder;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                _logger.LogWarning(ex, "Dense rerank model failed to load; the query path stays lexical.");
                return null;
            }
        }
    }

    // Embeds a document's text, caching the result by content hash so a warm rerank over an unchanged file
    // does not re-run the model (item 23). Empty text is not cached (it is a cheap, degenerate case).
    private float[] EmbedDocumentCached(MiniLmEmbedder embedder, string text)
    {
        if (text.Length == 0)
            return embedder.Embed(text);

        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text));
        if (_embeddingCache.TryGetValue(hash, out var cached))
            return cached;

        var vector = embedder.Embed(text);
        if (_embeddingCache.Count >= MaxCachedEmbeddings)
            _embeddingCache.Clear();
        _embeddingCache[hash] = vector;
        return vector;
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

    // Rescales values to [0, 1] over the pool in place. A degenerate pool (all equal) maps to all zeros, so a
    // signal with no spread contributes nothing to the blend rather than dominating it.
    private static void MinMaxNormalize(double[] values)
    {
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var v in values)
        {
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var range = max - min;
        if (range <= 0)
        {
            Array.Clear(values);
            return;
        }

        for (var i = 0; i < values.Length; i++)
            values[i] = (values[i] - min) / range;
    }

    /// <inheritdoc />
    public void Dispose() => _embedder?.Dispose();
}
