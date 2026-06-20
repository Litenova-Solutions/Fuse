namespace Fuse.Fusion.Retrieval;

/// <summary>
///     Produces a fixed-length numeric vector for a piece of text, used by the hybrid reranker to score the
///     similarity between a query and a candidate file.
/// </summary>
/// <remarks>
///     The default implementation is a deterministic lexical hashing embedding that needs no model file or
///     network, so it is Native AOT compatible. The interface is the extension point for a learned embedding
///     model: replacing the registration swaps in semantic vectors without changing the reranker. Vectors are
///     L2-normalized, so similarity is the dot product.
/// </remarks>
public interface IEmbeddingModel
{
    /// <summary>The dimensionality of the vectors this model produces.</summary>
    int Dimensions { get; }

    /// <summary>
    ///     Embeds text into an L2-normalized vector of length <see cref="Dimensions" />.
    /// </summary>
    /// <param name="text">The text to embed. A null or empty value yields a zero vector.</param>
    /// <returns>The embedding vector.</returns>
    float[] Embed(string text);
}
