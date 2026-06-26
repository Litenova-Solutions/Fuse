using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Fuse.Plugins.Rerank.Onnx;

// Wraps a ms-marco-MiniLM-L-6-v2 cross-encoder: a BERT WordPiece tokenizer plus an ONNX Runtime session over a
// BertForSequenceClassification graph. Unlike the bi-encoder (which embeds query and document independently and
// compares vectors), a cross-encoder runs the model over the concatenated [CLS] query [SEP] document [SEP] pair
// and emits a single relevance logit, so the query attends to the document directly. That is more accurate but
// costs one model run per candidate (no document-side caching is possible, since the score is pair-specific).
// The session and tokenizer are stateless across calls and ONNX Runtime is safe for concurrent Run calls.
internal sealed class CrossEncoderScorer : IDisposable
{
    // BERT base context window for the pair. The query is kept whole and the document is truncated to fit, since
    // the query is short and the document is the long side.
    private const int MaxTokens = 512;

    // Standard BERT special-token ids for this 30522-entry WordPiece vocabulary (the same vocab the bi-encoder
    // loads): [CLS] opens the sequence, [SEP] separates the two segments and closes the pair.
    private const long ClsId = 101;
    private const long SepId = 102;

    private readonly BertTokenizer _tokenizer;
    private readonly InferenceSession _session;
    private readonly string _outputName;
    private readonly bool _needsTokenTypeIds;

    private CrossEncoderScorer(BertTokenizer tokenizer, InferenceSession session)
    {
        _tokenizer = tokenizer;
        _session = session;
        _outputName = session.OutputMetadata.Keys.First();
        _needsTokenTypeIds = session.InputMetadata.ContainsKey("token_type_ids");
    }

    // Loads the tokenizer vocabulary and the ONNX model from disk. Throws if either file is missing or invalid;
    // callers gate on file presence first and treat a load failure as "model unavailable".
    public static CrossEncoderScorer Load(string onnxModelPath, string vocabPath)
    {
        var tokenizer = BertTokenizer.Create(vocabPath);
        var session = new InferenceSession(onnxModelPath);
        return new CrossEncoderScorer(tokenizer, session);
    }

    // Scores one (query, document) pair to a single relevance logit; higher means more relevant. The query side
    // is encoded once by the caller and passed in as bare token ids (no CLS/SEP) to avoid re-tokenizing it for
    // every candidate.
    public double Score(IReadOnlyList<int> queryIds, string document)
    {
        var docIds = StripSpecial(_tokenizer.EncodeToIds(document ?? string.Empty));

        // Layout: [CLS] query [SEP] document [SEP]. Reserve three slots for the markers, keep the query whole,
        // and truncate the document to whatever budget remains.
        var reserved = 3 + queryIds.Count;
        var docBudget = Math.Max(0, MaxTokens - reserved);
        var docCount = Math.Min(docIds.Count, docBudget);
        var length = reserved + docCount;

        var inputIds = new long[length];
        var tokenTypeIds = _needsTokenTypeIds ? new long[length] : null;
        var attentionMask = new long[length];

        var pos = 0;
        inputIds[pos] = ClsId;
        attentionMask[pos] = 1L;
        pos++;
        foreach (var id in queryIds)
        {
            inputIds[pos] = id;
            attentionMask[pos] = 1L;
            pos++;
        }

        inputIds[pos] = SepId;
        attentionMask[pos] = 1L;
        pos++;

        // The document segment (and its closing [SEP]) carry token type 1; everything before stays 0.
        for (var i = 0; i < docCount; i++)
        {
            inputIds[pos] = docIds[i];
            attentionMask[pos] = 1L;
            if (tokenTypeIds is not null) tokenTypeIds[pos] = 1L;
            pos++;
        }

        inputIds[pos] = SepId;
        attentionMask[pos] = 1L;
        if (tokenTypeIds is not null) tokenTypeIds[pos] = 1L;

        var dimensions = new[] { 1, length };
        var feed = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, dimensions)),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, dimensions)),
        };
        if (tokenTypeIds is not null)
            feed.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(tokenTypeIds, dimensions)));

        using var results = _session.Run(feed);
        var logits = results.First(r => r.Name == _outputName).AsEnumerable<float>().ToArray();

        // BertForSequenceClassification with one label emits a single logit; that scalar is the relevance score.
        return logits.Length == 0 ? 0.0 : logits[0];
    }

    // Tokenizes the query once into bare ids (CLS/SEP stripped) for reuse across every candidate in a pool.
    public IReadOnlyList<int> EncodeQuery(string query) => StripSpecial(_tokenizer.EncodeToIds(query ?? string.Empty));

    // Drops the leading [CLS] and trailing [SEP] that the tokenizer adds, leaving the bare content ids so a
    // pair can be assembled as [CLS] query [SEP] document [SEP].
    private static List<int> StripSpecial(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0)
            return [];

        var start = ids[0] == ClsId ? 1 : 0;
        var end = ids[^1] == SepId ? ids.Count - 1 : ids.Count;
        var stripped = new List<int>(Math.Max(0, end - start));
        for (var i = start; i < end; i++)
            stripped.Add(ids[i]);
        return stripped;
    }

    public void Dispose() => _session.Dispose();
}
