namespace Fuse.Scoping;

/// <summary>
///     Greedy token-budget packing for context plans.
/// </summary>
public static class ContextPlanPacker
{
    /// <summary>
    ///     Orders items by must-keep first, then score, then path, and drops optional items that exceed the
    ///     budget.
    /// </summary>
    /// <param name="items">The plan items to pack.</param>
    /// <param name="maxTokens">The token budget, or <c>null</c> for no limit.</param>
    /// <param name="warnings">Warnings to append to when items are dropped.</param>
    /// <returns>The packed items in ranked order.</returns>
    public static List<ContextPlanItem> Pack(
        List<ContextPlanItem> items,
        int? maxTokens,
        List<string> warnings)
    {
        var ordered = items
            .OrderByDescending(i => i.MustKeep)
            .ThenByDescending(i => i.Score)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .ToList();

        if (maxTokens is not { } budget)
            return ordered;

        var kept = new List<ContextPlanItem>();
        var used = 0;
        var dropped = 0;
        foreach (var item in ordered)
        {
            if (item.MustKeep || used + item.EstimatedTokens <= budget)
            {
                kept.Add(item);
                used += item.EstimatedTokens;
            }
            else
            {
                dropped++;
            }
        }

        if (dropped > 0)
            warnings.Add($"{dropped} file(s) dropped to fit the {budget} token budget.");

        return kept;
    }
}
