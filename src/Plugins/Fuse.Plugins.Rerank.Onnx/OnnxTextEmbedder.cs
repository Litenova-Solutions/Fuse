using Fuse.Plugins.Abstractions.Scoping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fuse.Plugins.Rerank.Onnx;

/// <summary>
///     An <see cref="ITextEmbedder" /> backed by the in-process all-MiniLM-L6-v2 sentence model, used to
///     persist a per-chunk embedding index at indexing time and to embed a query at retrieval time.
/// </summary>
/// <remarks>
///     The model loads lazily on first use and is then reused (ONNX Runtime sessions are safe for concurrent
///     inference). When the model is absent or fails to load, <see cref="IsAvailable" /> reports false and the
///     retrieval path stays lexical, so this never removes the no-model floor.
/// </remarks>
public sealed class OnnxTextEmbedder : ITextEmbedder, IDisposable
{
    // all-MiniLM-L6-v2 produces 384-dimensional sentence vectors.
    private const int VectorDimension = 384;

    private readonly string _onnxModelPath;
    private readonly string _vocabPath;
    private readonly ILogger<OnnxTextEmbedder> _logger;
    private readonly object _loadGate = new();

    private MiniLmEmbedder? _embedder;
    private bool _loadFailed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OnnxTextEmbedder" /> class over the cached model files.
    /// </summary>
    /// <param name="onnxModelPath">Path to the quantized ONNX graph.</param>
    /// <param name="vocabPath">Path to the WordPiece vocabulary.</param>
    /// <param name="logger">Optional logger; a load failure is logged once and then the embedder stays unavailable.</param>
    public OnnxTextEmbedder(string onnxModelPath, string vocabPath, ILogger<OnnxTextEmbedder>? logger = null)
    {
        _onnxModelPath = onnxModelPath;
        _vocabPath = vocabPath;
        _logger = logger ?? NullLogger<OnnxTextEmbedder>.Instance;
    }

    /// <summary>
    ///     Creates an embedder over the model in the Fuse user-data cache, present or not. The instance reports
    ///     unavailable until the model files exist, so this is safe to construct unconditionally.
    /// </summary>
    /// <param name="logger">An optional logger.</param>
    /// <returns>An embedder bound to the default cached model location.</returns>
    public static OnnxTextEmbedder CreateDefault(ILogger<OnnxTextEmbedder>? logger = null) =>
        new(RerankModelLocator.OnnxModelPath(), RerankModelLocator.VocabPath(), logger);

    /// <inheritdoc />
    public bool IsAvailable => !_loadFailed && (_embedder is not null || EnsureLoaded() is not null);

    /// <inheritdoc />
    public int Dimension => VectorDimension;

    /// <inheritdoc />
    public float[] Embed(string text)
    {
        var embedder = EnsureLoaded();
        if (embedder is null)
            return [];

        try
        {
            return embedder.Embed(text);
        }
        catch (Exception ex)
        {
            // A runtime inference failure must not break indexing or scoping: contribute no dense signal.
            _logger.LogWarning(ex, "Embedding failed; treating as no dense signal.");
            return [];
        }
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
                _logger.LogWarning(ex, "Embedding model failed to load; the retrieval path stays lexical.");
                return null;
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _embedder?.Dispose();
}
