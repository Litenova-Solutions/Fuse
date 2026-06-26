namespace Fuse.Retrieval;

/// <summary>
///     Normalizes raw candidates into a deduplicated, ranked set: candidates for the same node or file are
///     merged and their corroborating sources combined.
/// </summary>
/// <remarks>
///     Candidates are grouped by node id when present, otherwise by file path. A group's score combines its
///     sources with a noisy-or (<c>1 - product(1 - score)</c>), which stays in 0 to 1 and rewards corroboration
///     without exceeding the strongest single signal's ceiling. The result is ordered by score descending, then
///     by path for determinism.
/// </remarks>
public sealed class CandidateScorer
{
    /// <summary>
    ///     Merges and ranks candidates.
    /// </summary>
    /// <param name="candidates">The raw candidates from the generators.</param>
    /// <returns>The deduplicated, ranked candidates.</returns>
    public IReadOnlyList<ScoredCandidate> Score(IReadOnlyList<CandidateNode> candidates)
    {
        var groups = new Dictionary<string, List<CandidateNode>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var key = string.IsNullOrEmpty(candidate.NodeId) ? "file:" + candidate.FilePath : candidate.NodeId;
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = [];
            list.Add(candidate);
        }

        var scored = new List<ScoredCandidate>(groups.Count);
        foreach (var group in groups.Values)
        {
            var primary = group.MaxBy(c => c.BaseScore)!;
            var score = NoisyOr(group.Select(c => c.BaseScore));
            var sources = group.Select(c => c.Source).Distinct().ToList();
            var reasons = group.SelectMany(c => c.Reasons).Distinct(StringComparer.Ordinal).ToList();
            var tokenEstimate = group.Max(c => c.TokenEstimate);

            scored.Add(new ScoredCandidate(
                NodeId: primary.NodeId,
                FilePath: primary.FilePath,
                Kind: primary.Kind,
                Score: score,
                Sources: sources,
                Reasons: reasons,
                TokenEstimate: tokenEstimate));
        }

        return scored
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.FilePath, StringComparer.Ordinal)
            .ToList();
    }

    // Combine independent probabilities so that more corroborating sources raise the score toward, but never
    // past, 1. A single source returns its own score unchanged.
    private static double NoisyOr(IEnumerable<double> scores)
    {
        var product = 1.0;
        foreach (var score in scores)
            product *= 1.0 - Math.Clamp(score, 0.0, 1.0);
        return 1.0 - product;
    }
}

/// <summary>
///     A merged, ranked candidate: the strongest representative plus the combined score and provenance.
/// </summary>
/// <param name="NodeId">The graph node id, or empty for a file-only candidate.</param>
/// <param name="FilePath">The candidate's normalized file path.</param>
/// <param name="Kind">The candidate kind.</param>
/// <param name="Score">The combined, normalized score (0 to 1).</param>
/// <param name="Sources">The distinct sources that produced the candidate.</param>
/// <param name="Reasons">The combined inclusion reasons.</param>
/// <param name="TokenEstimate">The estimated token cost, when known.</param>
public sealed record ScoredCandidate(
    string NodeId,
    string FilePath,
    string Kind,
    double Score,
    IReadOnlyList<CandidateSource> Sources,
    IReadOnlyList<string> Reasons,
    int TokenEstimate);
