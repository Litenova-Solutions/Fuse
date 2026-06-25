namespace Fuse.Fusion.Scoping;

using Fuse.Plugins.Abstractions.Scoping;

/// <summary>
///     Combines several ranked file lists into one using Reciprocal Rank Fusion (RRF), a rank-only combiner that
///     needs no score calibration across the input rankings.
/// </summary>
/// <remarks>
///     RRF scores a path as the sum, over each input ranking that contains it, of <c>1 / (k + rank)</c> where
///     <c>rank</c> is 1-based. The constant <c>k</c> (default 60, the value from the original RRF paper) damps
///     the influence of the very top ranks so that a file ranked highly by several variants outranks one ranked
///     first by a single variant. Because only ranks enter the formula, rankings produced by different scorers
///     (a raw BM25F pass, a pseudo-relevance-expanded pass, an identifier-only pass) combine without
///     normalizing their incomparable raw scores. Ties break by the best (lowest) rank any input gave the path,
///     then by path for determinism.
/// </remarks>
public static class RankFusion
{
    /// <summary>
    ///     The default RRF damping constant, from the original Reciprocal Rank Fusion paper.
    /// </summary>
    public const int DefaultK = 60;

    /// <summary>
    ///     Fuses the supplied rankings into a single ranking by Reciprocal Rank Fusion.
    /// </summary>
    /// <param name="rankings">
    ///     The rankings to combine, each an ordered list of <see cref="RankedFile" /> from most to least relevant.
    ///     Empty rankings and null entries are ignored. A path's own score within a ranking is not used; only its
    ///     position is.
    /// </param>
    /// <param name="topN">The maximum number of fused results to return.</param>
    /// <param name="k">The RRF damping constant; defaults to <see cref="DefaultK" />.</param>
    /// <returns>
    ///     The fused ranking, most relevant first, carrying the RRF score, truncated to <paramref name="topN" />.
    /// </returns>
    public static IReadOnlyList<RankedFile> Fuse(
        IEnumerable<IReadOnlyList<RankedFile>?> rankings,
        int topN,
        int k = DefaultK)
    {
        var fusedScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var bestRank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var ranking in rankings)
        {
            if (ranking is null)
                continue;

            for (var i = 0; i < ranking.Count; i++)
            {
                var path = ranking[i].Path;
                var rank = i + 1;
                fusedScores.TryGetValue(path, out var current);
                fusedScores[path] = current + 1.0 / (k + rank);

                if (!bestRank.TryGetValue(path, out var prior) || rank < prior)
                    bestRank[path] = rank;
            }
        }

        if (fusedScores.Count == 0 || topN <= 0)
            return [];

        return fusedScores
            .Select(kv => new RankedFile(kv.Key, kv.Value))
            .OrderByDescending(r => r.Score)
            .ThenBy(r => bestRank[r.Path])
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Take(topN)
            .ToArray();
    }
}
