using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction.Tokenization;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     Default <see cref="ITokenCostModel" />: a per-level retention factor applied to the raw token count,
///     calibrated from the Layer 1 benchmark reduction ratios over the pinned corpus.
/// </summary>
/// <remarks>
///     C# structural reduction removes roughly 7 to 10 percent at the default and standard levels and 21 to 46
///     percent aggressive; the retention factors for those levels are the rough midpoints of the measured
///     ranges. The skeleton factor (0.15) is a deliberately conservative pre-Roslyn estimate: the Roslyn
///     skeleton keeps every signature and so removes only 39 to 56 percent in practice, so this factor
///     under-counts skeleton cost. It is left conservative on purpose (a low estimate admits more neighbours
///     for the budget-aware packer to trim) and any recalibration must be re-measured against Layer 2A. Non-C#
///     files only get whitespace normalization, so they retain almost all their tokens at every level. The
///     estimate is intentionally cheap (one token count plus a multiply); the real count from
///     <see cref="CountTokens" /> is used wherever exact accounting is required.
/// </remarks>
public sealed class DefaultTokenCostModel : ITokenCostModel
{
    private readonly ITokenCounter _tokenCounter;

    /// <summary>
    ///     Initializes a new instance of the <see cref="DefaultTokenCostModel" /> class.
    /// </summary>
    /// <param name="tokenCounter">The token counter used for the raw count and exact counts.</param>
    public DefaultTokenCostModel(ITokenCounter tokenCounter)
    {
        _tokenCounter = tokenCounter;
    }

    /// <inheritdoc />
    public int EstimateReducedTokens(string content, string extension, ReductionLevel level)
    {
        if (string.IsNullOrEmpty(content))
            return 0;

        var raw = _tokenCounter.Count(content);
        var retention = RetentionFactor(extension, level);
        return (int)Math.Round(raw * retention, MidpointRounding.AwayFromZero);
    }

    /// <inheritdoc />
    public int CountTokens(string content) =>
        string.IsNullOrEmpty(content) ? 0 : _tokenCounter.Count(content);

    // Fraction of raw tokens expected to survive reduction. C# structural reduction is the only level-sensitive
    // reducer; other extensions get whitespace normalization only.
    private static double RetentionFactor(string extension, ReductionLevel level)
    {
        var isCSharp = string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase);
        if (!isCSharp)
            return 0.95;

        return level switch
        {
            ReductionLevel.None => 0.92,
            ReductionLevel.Standard => 0.92,
            ReductionLevel.Aggressive => 0.70,
            ReductionLevel.Skeleton => 0.15,
            ReductionLevel.PublicApi => 0.10,
            _ => 0.92,
        };
    }
}
