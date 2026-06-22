using Fuse.Reduction.Models;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Selects which reduced entries to emit under a token budget using their real reduced token cost rather
///     than a pre-reduction byte estimate. This removes the dual-budget mismatch where expansion counted raw
///     bytes (which reduction then cut by 40 to 70 percent) and left the post-reduction budget under-filled.
/// </summary>
/// <remarks>
///     Selection is a greedy 0/1-knapsack approximation: the single most relevant entry is admitted first so a
///     scoped run never emits nothing, then the rest are admitted in descending relevance-per-token density
///     until the budget is full, skipping any that would overflow so smaller later entries can still fit. The
///     emission pipeline orders the surviving entries by relevance for output. Trivial entries are kept and not
///     charged, matching emission, which skips them.
/// </remarks>
internal static class ReductionAwarePacker
{
    /// <summary>
    ///     Returns the subset of <paramref name="entries" /> that fits within <paramref name="maxTokens" />.
    /// </summary>
    /// <param name="entries">The reduced entries, each carrying a relevance score and real token count.</param>
    /// <param name="maxTokens">The hard token budget the emitted entries must fit within.</param>
    /// <param name="markerOverhead">Per-entry marker overhead charged on top of each entry's token count.</param>
    public static IReadOnlyList<FusedContent> Pack(
        IReadOnlyList<FusedContent> entries,
        int maxTokens,
        int markerOverhead)
    {
        var kept = new List<FusedContent>();
        var candidates = new List<FusedContent>();
        foreach (var entry in entries)
        {
            if (entry.IsTrivial)
                kept.Add(entry);
            else
                candidates.Add(entry);
        }

        if (candidates.Count == 0)
            return entries;

        int Cost(FusedContent entry) => entry.TokenCount + markerOverhead;

        var byRelevance = candidates
            .OrderByDescending(e => e.RelevanceScore ?? double.NegativeInfinity)
            .ThenByDescending(e => e.TokenCount)
            .ThenBy(e => e.NormalizedPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The most relevant entry is admitted unconditionally, even if it alone exceeds the budget, so the
        // closest match to the query or seed always survives.
        var used = Cost(byRelevance[0]);
        kept.Add(byRelevance[0]);

        var byDensity = byRelevance
            .Skip(1)
            .OrderByDescending(e => (e.RelevanceScore ?? 0.0) / Math.Max(1, Cost(e)))
            .ThenByDescending(e => e.RelevanceScore ?? double.NegativeInfinity)
            .ThenBy(e => e.NormalizedPath, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in byDensity)
        {
            var cost = Cost(entry);
            if (used + cost > maxTokens)
                continue; // Skip this one but keep scanning: a smaller later entry may still fit.

            kept.Add(entry);
            used += cost;
        }

        return kept;
    }
}
