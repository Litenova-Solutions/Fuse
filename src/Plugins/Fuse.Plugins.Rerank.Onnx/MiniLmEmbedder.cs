using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Fuse.Plugins.Rerank.Onnx;

// Wraps an all-MiniLM-L6-v2 sentence embedder: a BERT WordPiece tokenizer plus an ONNX Runtime session.
// Embed produces a 384-d mean-pooled, L2-normalized sentence vector, so cosine similarity is a dot product.
// The ONNX session and the tokenizer are stateless across calls and ONNX Runtime sessions are safe for
// concurrent Run calls, so a single instance is shared across the server's concurrent queries.
internal sealed class MiniLmEmbedder : IDisposable
{
    // BERT base context window. Inputs are truncated to this many tokens (including the CLS/SEP markers).
    private const int MaxTokens = 256;

    private readonly BertTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly string _outputName;
    private readonly bool _needsTokenTypeIds;

    private MiniLmEmbedder(BertTokenizer tokenizer, InferenceSession session)
    {
        _tokenizer = tokenizer;
        _session = session;
        _outputName = session.OutputMetadata.Keys.First();
        _needsTokenTypeIds = session.InputMetadata.ContainsKey("token_type_ids");
    }

    // Loads the tokenizer vocabulary and the ONNX model from disk. Throws if either file is missing or invalid;
    // callers gate on file presence first and treat a load failure as "model unavailable".
    public static MiniLmEmbedder Load(string onnxModelPath, string vocabPath)
    {
        var tokenizer = BertTokenizer.Create(vocabPath);
        var session = new InferenceSession(onnxModelPath);
        return new MiniLmEmbedder(tokenizer, session);
    }

    // Embeds a single text into a unit-length 384-d vector. An empty input yields a zero vector, whose cosine
    // with anything is zero, so an empty candidate simply contributes no dense signal.
    public float[] Embed(string text)
    {
        var ids = _tokenizer.EncodeToIds(text ?? string.Empty);
        var count = Math.Min(ids.Count, MaxTokens);
        if (count == 0)
            return [];

        var inputIds = new long[count];
        var attentionMask = new long[count];
        var tokenTypeIds = _needsTokenTypeIds ? new long[count] : null;
        for (var i = 0; i < count; i++)
        {
            inputIds[i] = ids[i];
            attentionMask[i] = 1L;
        }

        var dimensions = new[] { 1, count };
        var feed = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, dimensions)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, dimensions)),
        };
        if (tokenTypeIds is not null)
            feed.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, dimensions)));

        using var results = _session.Run(feed);
        var hidden = results.First(r => r.Name == _outputName).AsTensor<float>();
        var width = hidden.Dimensions[^1];

        // Mean-pool over the sequence (the attention mask is all ones for the truncated, unpadded input), then
        // L2-normalize so a later dot product is a cosine similarity.
        var embedding = new float[width];
        for (var token = 0; token < count; token++)
            for (var d = 0; d < width; d++)
                embedding[d] += hidden[0, token, d];

        var norm = 0.0;
        for (var d = 0; d < width; d++)
        {
            embedding[d] /= count;
            norm += embedding[d] * (double)embedding[d];
        }

        norm = Math.Sqrt(norm);
        if (norm > 0)
            for (var d = 0; d < width; d++)
                embedding[d] = (float)(embedding[d] / norm);

        return embedding;
    }

    public void Dispose() => _session.Dispose();
}
