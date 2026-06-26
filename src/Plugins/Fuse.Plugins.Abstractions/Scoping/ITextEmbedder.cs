namespace Fuse.Plugins.Abstractions.Scoping;

/// <summary>
///     Embeds text into a dense vector so two pieces of text can be compared by meaning rather than by shared
///     words. Used to persist a per-chunk embedding index at indexing time and to embed a query at retrieval
///     time, so a prose query can rank a chunk it does not share tokens with.
/// </summary>
/// <remarks>
///     This is an optional capability: when no implementation is registered (no model present, offline, or the
///     feature is off) the retrieval path stays on the lexical BM25F ranking, which is the guaranteed no-model,
///     no-network floor. An implementation must be deterministic for a given model and input, must return a
///     unit-length vector of <see cref="Dimension" /> components (so a dot product is a cosine similarity), and
///     must return an empty array for empty or untokenizable input rather than throwing.
/// </remarks>
public interface ITextEmbedder
{
    /// <summary>
    ///     Whether the embedder is ready to embed (its model is loaded or loadable). When
    ///     <see langword="false" />, callers skip embedding and keep the lexical ranking.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>The dimension of the vectors this embedder produces.</summary>
    int Dimension { get; }

    /// <summary>
    ///     Embeds a single text into a unit-length dense vector.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <returns>
    ///     A unit-length vector of <see cref="Dimension" /> components, or an empty array for empty or
    ///     untokenizable input.
    /// </returns>
    float[] Embed(string text);
}
