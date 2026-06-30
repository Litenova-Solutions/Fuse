using Fuse.Plugins.Abstractions.Scoping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fuse.Plugins.Rerank.Onnx;

/// <summary>
///     An <see cref="IReranker" /> that reorders the BM25 candidate pool with an in-process cross-encoder
///     (ms-marco-MiniLM-L-6-v2), scoring each query-to-document pair jointly so the query attends directly to a
///     candidate's text rather than comparing two independently pooled embeddings.
/// </summary>
/// <remarks>
///     A cross-encoder is the more accurate reranker family but costs one model run per candidate (its score is
///     pair-specific, so no document-side embedding can be cached). The model loads lazily on the first rerank
///     and is reused across calls; when it is absent or fails to load the reranker reports unavailable and the
///     lexical ordering is kept, so retrieval stays on the lexical path when no model is present. The cross-encoder logit and the
///     lexical score are min-max normalized over the pool before blending, so neither signal's raw scale leaks
///     into the blend.
/// </remarks>
public sealed class OnnxCrossEncoderReranker : IReranker, IDisposable
{
    // Weight on the lexical (BM25) signal in the blend; the remainder is the cross-encoder relevance signal. An
    // even blend keeps a strong exact-term match competitive while letting the model break ties and rescue a
    // vocabulary-mismatched file, mirroring the bi-encoder blend so the two rerankers are compared like for like.
    private const double LexicalWeight = 0.5;

    private readonly string _onnxModelPath;
    private readonly string _vocabPath;
    private readonly ILogger<OnnxCrossEncoderReranker> _logger;
    private readonly object _loadGate = new();

    private CrossEncoderScorer? _scorer;
    private bool _loadFailed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OnnxCrossEncoderReranker" /> class over the cached model files.
    /// </summary>
    /// <param name="onnxModelPath">Path to the quantized ONNX graph.</param>
    /// <param name="vocabPath">Path to the WordPiece vocabulary.</param>
    /// <param name="logger">Optional logger; a load failure is logged once and then the reranker stays unavailable.</param>
    public OnnxCrossEncoderReranker(string onnxModelPath, string vocabPath, ILogger<OnnxCrossEncoderReranker>? logger = null)
    {
        _onnxModelPath = onnxModelPath;
        _vocabPath = vocabPath;
        _logger = logger ?? NullLogger<OnnxCrossEncoderReranker>.Instance;
    }

    /// <inheritdoc />
    public bool IsAvailable => !_loadFailed && (_scorer is not null || EnsureLoaded() is not null);

    /// <inheritdoc />
    public IReadOnlyList<RankedFile> Rerank(
        string query,
        IReadOnlyList<RankedFile> candidates,
        IReadOnlyDictionary<string, string> documentText)
    {
        if (candidates.Count < 2)
            return candidates;

        var scorer = EnsureLoaded();
        if (scorer is null)
            return candidates;

        var relevance = new double[candidates.Count];
        var lexical = new double[candidates.Count];
        try
        {
            var queryIds = scorer.EncodeQuery(query);
            if (queryIds.Count == 0)
                return candidates;

            for (var i = 0; i < candidates.Count; i++)
            {
                lexical[i] = candidates[i].Score;
                var text = documentText.TryGetValue(candidates[i].Path, out var t) ? t : string.Empty;
                relevance[i] = scorer.Score(queryIds, text);
            }
        }
        catch (Exception ex)
        {
            // A runtime inference failure must not break scoping: fall back to the lexical order.
            _logger.LogWarning(ex, "Cross-encoder rerank failed; keeping lexical order.");
            return candidates;
        }

        MinMaxNormalize(lexical);
        MinMaxNormalize(relevance);

        var reranked = new List<RankedFile>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var blended = LexicalWeight * lexical[i] + (1 - LexicalWeight) * relevance[i];
            reranked.Add(candidates[i] with { Score = blended });
        }

        return reranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private CrossEncoderScorer? EnsureLoaded()
    {
        if (_scorer is not null)
            return _scorer;
        if (_loadFailed)
            return null;

        lock (_loadGate)
        {
            if (_scorer is not null)
                return _scorer;
            if (_loadFailed)
                return null;

            if (!File.Exists(_onnxModelPath) || !File.Exists(_vocabPath))
            {
                _loadFailed = true;
                return null;
            }

            try
            {
                _scorer = CrossEncoderScorer.Load(_onnxModelPath, _vocabPath);
                return _scorer;
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                _logger.LogWarning(ex, "Cross-encoder model failed to load; the query path stays lexical.");
                return null;
            }
        }
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
    public void Dispose() => _scorer?.Dispose();
}
