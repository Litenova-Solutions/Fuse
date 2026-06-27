using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     A structural prior that boosts a candidate when it co-changes (in git history) with a stronger candidate,
///     so the sibling files of a multi-file change are recovered: a file that historically changes alongside a
///     strong hit is nudged up, even when it shares little query vocabulary. The prior is a small, capped
///     multiplier on the existing score, so it tunes the ranking rather than dominating it, and it cannot promote
///     a near-zero-score file on co-change alone.
/// </summary>
/// <remarks>
///     Co-change couplings are mined from a bounded git-history window at index time
///     (see the git co-change collector). When no couplings were mined (no git history, a non-repository, or the
///     miner found nothing above its floor) the prior is empty and scoring is unchanged. It reads over the
///     language-agnostic file paths, so it carries to any language.
/// </remarks>
public sealed class GitCoChangePrior
{
    /// <summary>The maximum fractional boost a strongly-coupled candidate receives (a capped, tuning-only multiplier).</summary>
    public const double CoChangeWeight = 0.15;

    // The leading candidates treated as "strong" seeds whose co-changers are boosted, and the bound on how far
    // down the ranking the prior adjusts, so the per-call work stays small.
    private const int StrongSeedCount = 8;
    private const int MaxCandidatesToAdjust = 30;

    private readonly IWorkspaceIndexStore _store;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GitCoChangePrior" /> class.
    /// </summary>
    /// <param name="store">The index store whose mined co-change table defines the couplings.</param>
    public GitCoChangePrior(IWorkspaceIndexStore store) => _store = store;

    /// <summary>
    ///     Applies the co-change multiplier to a ranked candidate set and re-sorts. When no co-change data was
    ///     mined the input is returned unchanged.
    /// </summary>
    /// <param name="ranked">The scored candidates, highest score first.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The candidates with the co-change prior blended in, re-sorted by score then path.</returns>
    public async Task<IReadOnlyList<ScoredCandidate>> ApplyAsync(
        IReadOnlyList<ScoredCandidate> ranked, CancellationToken cancellationToken)
    {
        if (ranked.Count == 0)
            return ranked;

        // The best score per candidate file, so a candidate is only boosted by a strictly stronger co-changer
        // (which prevents self-boost and circular boosting among equally-ranked files).
        var scoreByFile = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var candidate in ranked)
        {
            if (string.IsNullOrEmpty(candidate.FilePath))
                continue;
            if (!scoreByFile.TryGetValue(candidate.FilePath, out var existing) || candidate.Score > existing)
                scoreByFile[candidate.FilePath] = candidate.Score;
        }

        // The strong seeds whose co-changers we fetch: the leading candidate files (bounded so the IN-list query
        // stays small). A weaker candidate that co-changes with one of these is the recovery target.
        var seeds = ranked
            .Where(c => !string.IsNullOrEmpty(c.FilePath))
            .Take(StrongSeedCount)
            .Select(c => c.FilePath)
            .ToHashSet(StringComparer.Ordinal);
        if (seeds.Count == 0)
            return ranked;

        var couplings = await _store.GetCoChangesForAsync(seeds, cancellationToken);
        if (couplings.Count == 0)
            return ranked;

        // For each candidate file, the strongest Jaccard coupling it has to a seed file that outscores it.
        var strengthByFile = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var coupling in couplings)
        {
            Consider(seeds, scoreByFile, strengthByFile, seedSide: coupling.PathA, otherSide: coupling.PathB, coupling.Jaccard);
            Consider(seeds, scoreByFile, strengthByFile, seedSide: coupling.PathB, otherSide: coupling.PathA, coupling.Jaccard);
        }

        if (strengthByFile.Count == 0)
            return ranked;

        var adjusted = new List<ScoredCandidate>(ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            var candidate = ranked[i];
            if (i < MaxCandidatesToAdjust
                && !string.IsNullOrEmpty(candidate.FilePath)
                && strengthByFile.TryGetValue(candidate.FilePath, out var strength))
            {
                var boosted = Math.Min(1.0, candidate.Score * (1.0 + CoChangeWeight * strength));
                adjusted.Add(candidate with { Score = boosted });
            }
            else
            {
                adjusted.Add(candidate);
            }
        }

        return adjusted
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.FilePath, StringComparer.Ordinal)
            .ToList();
    }

    // Records the strongest coupling of a file to a seed file that outscores it: the seed side must be a fetched
    // seed, and it must score strictly higher than the side being boosted, so only a stronger hit lifts a sibling.
    private static void Consider(
        IReadOnlyCollection<string> seeds,
        IReadOnlyDictionary<string, double> scoreByFile,
        Dictionary<string, double> strengthByFile,
        string seedSide,
        string otherSide,
        double jaccard)
    {
        if (!seeds.Contains(seedSide))
            return;
        if (!scoreByFile.TryGetValue(seedSide, out var seedScore) || !scoreByFile.TryGetValue(otherSide, out var otherScore))
            return;
        if (seedScore <= otherScore)
            return;

        if (!strengthByFile.TryGetValue(otherSide, out var current) || jaccard > current)
            strengthByFile[otherSide] = jaccard;
    }
}
