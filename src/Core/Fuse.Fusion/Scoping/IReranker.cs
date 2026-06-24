namespace Fuse.Fusion.Scoping;

/// <summary>
///     Reorders a query's BM25 candidate pool by blending the lexical score with a dense query-to-document
///     similarity, so a file whose meaning matches the query can outrank one that merely shares more words.
/// </summary>
/// <remarks>
///     This is an optional capability: when no implementation is registered (no model present, offline, or the
///     feature is off) the query path runs on the lexical BM25F ranking alone, which is the guaranteed
///     no-model, no-network floor. An implementation embeds the query and each candidate's text representation
///     with a small in-process model and returns the candidates reordered by the blended score. It must be
///     deterministic for a given model and inputs, and must not throw for an empty or single-item pool.
/// </remarks>
public interface IReranker
{
    /// <summary>
    ///     Whether the reranker is ready to score (its model is loaded or loadable). When <see langword="false" />,
    ///     callers keep the lexical ordering rather than invoking <see cref="Rerank" />.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    ///     Reorders <paramref name="candidates" /> by blending each candidate's existing (lexical) score with the
    ///     dense similarity between <paramref name="query" /> and the candidate's text from
    ///     <paramref name="documentText" />.
    /// </summary>
    /// <param name="query">The raw query text.</param>
    /// <param name="candidates">The lexical candidate pool, each with its BM25 score in <see cref="RankedFile.Score" />.</param>
    /// <param name="documentText">
    ///     The text representation to embed per candidate path (for example declared symbols, the path, and a
    ///     content sketch). A path absent from the map is treated as empty text and keeps its lexical rank.
    /// </param>
    /// <returns>
    ///     The candidates reordered by the blended score, highest first. Returns the input order unchanged when
    ///     the reranker is unavailable or the pool has fewer than two items.
    /// </returns>
    IReadOnlyList<RankedFile> Rerank(
        string query,
        IReadOnlyList<RankedFile> candidates,
        IReadOnlyDictionary<string, string> documentText);
}
