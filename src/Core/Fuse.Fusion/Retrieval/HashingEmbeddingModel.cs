using System.IO.Hashing;
using System.Text;
using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Retrieval;

/// <summary>
///     A deterministic, dependency-free embedding that hashes tokens and their character trigrams into a fixed
///     number of buckets. Pure arithmetic with no model file or network.
/// </summary>
/// <remarks>
///     Including character trigrams lets the vector capture sub-word overlap, so a query term matches an
///     identifier that shares stems even when the whole tokens differ. This is a lexical signal: it sharpens
///     identifier matching but does not bridge a true semantic gap (a query sharing no words or sub-words with
///     the target). A learned <see cref="IEmbeddingModel" /> can be registered in its place to close that gap;
///     this model is the default when ONNX embeddings are not enabled.
/// </remarks>
public sealed class HashingEmbeddingModel : IEmbeddingModel
{
    private readonly int _dimensions;

    /// <summary>
    ///     Initializes a new instance of the <see cref="HashingEmbeddingModel" /> class.
    /// </summary>
    /// <param name="dimensions">The vector length; must be positive. Defaults to 256.</param>
    public HashingEmbeddingModel(int dimensions = 256)
    {
        _dimensions = dimensions > 0 ? dimensions : 256;
    }

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <inheritdoc />
    public float[] Embed(string text)
    {
        var vector = new float[_dimensions];
        if (string.IsNullOrEmpty(text))
            return vector;

        foreach (var term in RelevanceTokenizer.Tokenize(text))
        {
            Add(vector, term, 1f);
            foreach (var trigram in Trigrams(term))
                Add(vector, trigram, 0.5f);
        }

        Normalize(vector);
        return vector;
    }

    private void Add(float[] vector, string feature, float weight)
    {
        var hash = XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(feature));
        var bucket = (int)(hash % (uint)_dimensions);
        // Sign from a separate bit keeps unrelated features from always reinforcing each other.
        var sign = (hash & 0x8000_0000u) == 0 ? 1f : -1f;
        vector[bucket] += sign * weight;
    }

    private static IEnumerable<string> Trigrams(string term)
    {
        if (term.Length < 3)
            yield break;

        for (var i = 0; i + 3 <= term.Length; i++)
            yield return term.Substring(i, 3);
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
}
