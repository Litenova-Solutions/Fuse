namespace Fuse.Analysis.Search;

/// <summary>
///     Builds an in-memory relevance index and ranks files against a query.
/// </summary>
public interface IRelevanceIndex
{
    /// <summary>
    ///     Indexes the supplied file contents keyed by normalized relative path.
    /// </summary>
    /// <param name="fileContents">Map of normalized relative path to raw file content to index.</param>
    /// <remarks>Replaces any previously indexed content. Must be called before <see cref="Rank" />.</remarks>
    void Index(IReadOnlyDictionary<string, string> fileContents);

    /// <summary>
    ///     Ranks indexed files against the query using BM25 scoring.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="topN">Maximum number of ranked paths to return.</param>
    /// <returns>
    ///     Normalized relative paths ordered from most to least relevant, capped at <paramref name="topN" />.
    ///     Empty when nothing has been indexed, the query is blank, or no file matches any query term.
    /// </returns>
    IReadOnlyList<string> Rank(string query, int topN);
}
