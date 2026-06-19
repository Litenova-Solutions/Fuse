namespace Fuse.Analysis.Search;

/// <summary>
///     Builds an in-memory relevance index and ranks files against a query.
/// </summary>
public interface IRelevanceIndex
{
    /// <summary>
    ///     Indexes the supplied file contents keyed by normalized relative path.
    /// </summary>
    void Index(IReadOnlyDictionary<string, string> fileContents);

    /// <summary>
    ///     Ranks indexed files against the query using BM25 scoring.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="topN">Maximum number of ranked paths to return.</param>
    IReadOnlyList<string> Rank(string query, int topN);
}
