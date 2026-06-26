using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Estimates the token cost of a file before it is reduced and reports the real cost after, so the
///     scoping and packing stages reason about one consistent notion of token cost.
/// </summary>
/// <remarks>
///     Budget-aware expansion needs a per-file token estimate before any file is reduced, while packing works
///     from the real post-reduction count. This interface unifies the two: <see cref="EstimateReducedTokens" />
///     gives a cheap pre-reduction estimate at a given reduction level, and <see cref="CountTokens" /> gives the
///     exact count once content exists. The estimate is a heuristic (a per-level retention factor applied to the
///     raw token count) calibrated from the benchmark corpus, so it is an approximation, not a guarantee; the
///     direction (a skeleton costs far fewer tokens than a full body) is what budget-aware expansion relies on.
/// </remarks>
public interface ITokenCostModel
{
    /// <summary>
    ///     Estimates the token count <paramref name="content" /> would have after reduction at the given level,
    ///     without reducing it.
    /// </summary>
    /// <param name="content">The raw file content.</param>
    /// <param name="extension">The file extension (with leading dot), used to pick the retention profile.</param>
    /// <param name="level">The reduction level the file would be reduced at.</param>
    /// <returns>The estimated post-reduction token count, never negative.</returns>
    int EstimateReducedTokens(string content, string extension, ReductionLevel level);

    /// <summary>
    ///     Counts the exact tokens of already-produced content.
    /// </summary>
    /// <param name="content">The content to count.</param>
    /// <returns>The exact token count.</returns>
    int CountTokens(string content);
}
