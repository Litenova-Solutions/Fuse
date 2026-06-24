namespace Fuse.Fusion.Scoping;

/// <summary>
///     Options for BM25 query-scoped file selection.
/// </summary>
/// <param name="Query">The natural-language or keyword query.</param>
/// <param name="TopFiles">
///     Maximum seed files to select before dependency expansion. Also the default seed count when
///     <see cref="SeedTopK" /> is not set.
/// </param>
/// <param name="Depth">Dependency traversal depth after seed selection.</param>
/// <param name="CandidateTopK">
///     The number of files BM25 returns as the candidate pool, on which pseudo-relevance feedback and member
///     selection operate, before the seed set is taken. <c>0</c> (the default) means use <see cref="TopFiles" />,
///     reproducing the historical behavior where the candidate pool equals the seed set. A wider pool than the
///     seed set is what a reranking stage (a learned model) needs room to reorder; the lexical path leaves the
///     two equal so behavior is unchanged.
/// </param>
/// <param name="SeedTopK">
///     The number of top-ranked candidates promoted to expansion seeds. <c>0</c> (the default) means use
///     <see cref="TopFiles" />. Kept separate from <see cref="CandidateTopK" /> so the pool a reranker scores
///     can be wider than the set that is actually expanded from.
/// </param>
public sealed record QueryOptions(
    string Query,
    int TopFiles = 10,
    int Depth = 1,
    int CandidateTopK = 0,
    int SeedTopK = 0)
{
    /// <summary>
    ///     The resolved candidate-pool size: <see cref="CandidateTopK" /> when set, otherwise
    ///     <see cref="TopFiles" />. Never smaller than the resolved seed count.
    /// </summary>
    public int ResolvedCandidateTopK => Math.Max(ResolvedSeedTopK, CandidateTopK > 0 ? CandidateTopK : TopFiles);

    /// <summary>
    ///     The resolved seed count: <see cref="SeedTopK" /> when set, otherwise <see cref="TopFiles" />.
    /// </summary>
    public int ResolvedSeedTopK => SeedTopK > 0 ? SeedTopK : TopFiles;
}
