using Fuse.Fusion.Retrieval;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Fuse.Fusion.Embeddings.Onnx;

/// <summary>
///     A genuine semantic <see cref="IEmbeddingModel" /> backed by an ONNX sentence encoder (for example
///     <c>all-MiniLM-L6-v2</c>). It tokenizes text with the model's WordPiece vocabulary, runs the transformer,
///     mean-pools the token embeddings over the attention mask, and L2-normalizes the result, so cosine
///     similarity reflects semantic closeness rather than only lexical overlap.
/// </summary>
/// <remarks>
///     Inference is deterministic, so a repeated query yields an identical vector and a stable ranking. The
///     instance is thread-safe for concurrent <see cref="Embed" /> calls and owns the underlying session,
///     which is disposed with it.
/// </remarks>
public sealed class OnnxEmbeddingModel : IEmbeddingModel, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly int _dimensions;
    private readonly int _maxTokens;
    private readonly IReadOnlyList<string> _inputNames;

    /// <summary>
    ///     Initializes a new instance of the <see cref="OnnxEmbeddingModel" /> class from resolved local files.
    /// </summary>
    /// <param name="resolved">The resolved model and vocabulary paths plus the pinned descriptor.</param>
    public OnnxEmbeddingModel(ResolvedEmbeddingModel resolved)
    {
        _dimensions = resolved.Descriptor.Dimensions;
        _maxTokens = resolved.Descriptor.MaxTokens;
        _session = new InferenceSession(resolved.ModelPath);
        _inputNames = [.. _session.InputMetadata.Keys];
        using var vocab = File.OpenRead(resolved.VocabPath);
        _tokenizer = BertTokenizer.Create(vocab);
    }

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <inheritdoc />
    public float[] Embed(string text)
    {
        var pooled = new float[_dimensions];
        if (string.IsNullOrEmpty(text))
            return pooled;

        var ids = _tokenizer.EncodeToIds(text);
        var length = Math.Min(ids.Count, _maxTokens);
        if (length == 0)
            return pooled;

        var inputIds = new long[length];
        var mask = new long[length];
        for (var i = 0; i < length; i++)
        {
            inputIds[i] = ids[i];
            mask[i] = 1;
        }

        var inputs = BuildInputs(inputIds, mask, length);
        using var results = _session.Run(inputs);

        // The token-embedding output is the rank-3 tensor [batch, seq, dim]; mean-pool it over the mask.
        var hidden = results.First(r => r.AsTensor<float>().Dimensions.Length == 3).AsTensor<float>();
        MeanPool(hidden, length, pooled);
        Normalize(pooled);
        return pooled;
    }

    private List<NamedOnnxValue> BuildInputs(long[] inputIds, long[] mask, int length)
    {
        var idsTensor = new DenseTensor<long>(inputIds, [1, length]);
        var maskTensor = new DenseTensor<long>(mask, [1, length]);
        var typeTensor = new DenseTensor<long>(new long[length], [1, length]);

        var inputs = new List<NamedOnnxValue>(3);
        foreach (var name in _inputNames)
        {
            // Only supply the inputs this exported model actually declares; token_type_ids is optional.
            if (name is "input_ids")
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, idsTensor));
            else if (name is "attention_mask")
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, maskTensor));
            else if (name is "token_type_ids")
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, typeTensor));
        }

        return inputs;
    }

    // Mean of the token vectors weighted by the attention mask. With every masked-in token weight 1, this is
    // the arithmetic mean over the real (non-padding) tokens, the standard sentence-transformers pooling.
    private void MeanPool(Tensor<float> hidden, int length, float[] pooled)
    {
        for (var t = 0; t < length; t++)
        {
            for (var d = 0; d < _dimensions; d++)
                pooled[d] += hidden[0, t, d];
        }

        for (var d = 0; d < _dimensions; d++)
            pooled[d] /= length;
    }

    private static void Normalize(float[] vector)
    {
        double sumSquares = 0;
        foreach (var value in vector)
            sumSquares += value * (double)value;

        if (sumSquares <= 0)
            return;

        var inverse = (float)(1.0 / Math.Sqrt(sumSquares));
        for (var i = 0; i < vector.Length; i++)
            vector[i] *= inverse;
    }

    /// <inheritdoc />
    public void Dispose() => _session.Dispose();
}
