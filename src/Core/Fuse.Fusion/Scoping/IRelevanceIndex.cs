namespace Fuse.Fusion.Scoping;

/// <summary>
///     Builds an in-memory relevance index and ranks files against a query.
/// </summary>
public interface IRelevanceIndex
{
    /// <summary>
    ///     Indexes the supplied file contents keyed by normalized relative path, using only the body field.
    /// </summary>
    /// <param name="fileContents">Map of normalized relative path to raw file content to index.</param>
    /// <remarks>
    ///     Replaces any previously indexed content. Must be called before <see cref="Rank" />. Equivalent to
    ///     <see cref="Index(IReadOnlyDictionary{string, IndexedDocument}, Indexing.IRelevancePostingsStore)" /> with content-only documents.
    /// </remarks>
    void Index(IReadOnlyDictionary<string, string> fileContents);

    /// <summary>
    ///     Indexes the supplied fielded documents keyed by normalized relative path, optionally caching body
    ///     tokenization in a persistent store.
    /// </summary>
    /// <param name="documents">Map of normalized relative path to a fielded document to index.</param>
    /// <param name="postingsStore">
    ///     Optional store consulted for each document's body tokens keyed by content hash, and populated on a
    ///     miss, so a warm run skips re-tokenizing unchanged files. <c>null</c> tokenizes every body in memory
    ///     and produces output identical to the uncached path.
    /// </param>
    /// <remarks>Replaces any previously indexed content. Must be called before <see cref="Rank" />.</remarks>
    void Index(
        IReadOnlyDictionary<string, IndexedDocument> documents,
        Indexing.IRelevancePostingsStore? postingsStore = null);

    /// <summary>
    ///     Ranks indexed files against the query, returning paths only.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="topN">Maximum number of ranked paths to return.</param>
    /// <returns>
    ///     Normalized relative paths ordered from most to least relevant, capped at <paramref name="topN" />.
    ///     Empty when nothing has been indexed, the query is blank, or no file matches any query term.
    /// </returns>
    IReadOnlyList<string> Rank(string query, int topN);

    /// <summary>
    ///     Ranks indexed files against the query, returning paths with their relevance scores.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="topN">Maximum number of ranked files to return.</param>
    /// <returns>
    ///     Ranked files ordered from most to least relevant, capped at <paramref name="topN" />. Empty when
    ///     nothing has been indexed, the query is blank, or no file matches any query term.
    /// </returns>
    IReadOnlyList<RankedFile> RankScored(string query, int topN);
}
