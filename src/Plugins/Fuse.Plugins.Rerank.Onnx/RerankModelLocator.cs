namespace Fuse.Plugins.Rerank.Onnx;

/// <summary>
///     Resolves the on-disk location of the dense rerank model in the Fuse user-data cache, so the reranker can
///     check whether a model is present without downloading and load it when it is.
/// </summary>
/// <remarks>
///     The cache root is <c>FUSE_USER_DATA</c> when set, otherwise <c>~/.fuse</c>; the model lives under
///     <c>{root}/models/all-MiniLM-L6-v2</c> with the quantized ONNX graph at <c>onnx/model_quantized.onnx</c>
///     and the WordPiece vocabulary at <c>vocab.txt</c>. This type performs no network access: download is a
///     separate, explicit step, and an absent model means the query path stays lexical.
/// </remarks>
public static class RerankModelLocator
{
    /// <summary>The model identifier (and cache subdirectory name) of the default bi-encoder dense reranker.</summary>
    public const string ModelId = "all-MiniLM-L6-v2";

    /// <summary>The model identifier (and cache subdirectory name) of the cross-encoder reranker.</summary>
    public const string CrossEncoderModelId = "ms-marco-MiniLM-L-6-v2";

    /// <summary>
    ///     The directory holding the cached files for a model, whether or not they are present.
    /// </summary>
    /// <param name="modelId">The model identifier; defaults to the bi-encoder <see cref="ModelId" />.</param>
    /// <returns>The absolute path to <c>{userData}/models/{modelId}</c>.</returns>
    public static string ModelDirectory(string? modelId = null)
    {
        var root = Environment.GetEnvironmentVariable("FUSE_USER_DATA");
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fuse");

        return Path.Combine(root, "models", modelId ?? ModelId);
    }

    /// <summary>The expected path to a model's quantized ONNX graph.</summary>
    /// <param name="modelId">The model identifier; defaults to the bi-encoder <see cref="ModelId" />.</param>
    /// <returns>The absolute path to the model file, which may not exist.</returns>
    public static string OnnxModelPath(string? modelId = null) =>
        Path.Combine(ModelDirectory(modelId), "onnx", "model_quantized.onnx");

    /// <summary>The expected path to a model's WordPiece vocabulary.</summary>
    /// <param name="modelId">The model identifier; defaults to the bi-encoder <see cref="ModelId" />.</param>
    /// <returns>The absolute path to the vocabulary file, which may not exist.</returns>
    public static string VocabPath(string? modelId = null) => Path.Combine(ModelDirectory(modelId), "vocab.txt");

    /// <summary>
    ///     Whether both files of a model are present on disk and so the reranker can load without a download.
    /// </summary>
    /// <param name="modelId">The model identifier; defaults to the bi-encoder <see cref="ModelId" />.</param>
    /// <returns><see langword="true" /> when the ONNX graph and vocabulary both exist; otherwise <see langword="false" />.</returns>
    public static bool IsModelPresent(string? modelId = null) =>
        File.Exists(OnnxModelPath(modelId)) && File.Exists(VocabPath(modelId));
}
