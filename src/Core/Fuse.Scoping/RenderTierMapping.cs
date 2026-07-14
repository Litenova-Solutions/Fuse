namespace Fuse.Scoping;

/// <summary>
///     Maps between <see cref="RenderTier" /> and the reduction levels used by the fusion reduction pipeline.
/// </summary>
public static class RenderTierMapping
{
    /// <summary>Maps a render tier to the reduction level the reduction pipeline applies.</summary>
    /// <param name="tier">The render tier from the plan.</param>
    /// <returns>The equivalent reduction level.</returns>
    public static Plugins.Abstractions.Options.ReductionLevel ToReductionLevel(RenderTier tier) => tier switch
    {
        RenderTier.FullSource => Plugins.Abstractions.Options.ReductionLevel.None,
        RenderTier.Reduced => Plugins.Abstractions.Options.ReductionLevel.Standard,
        RenderTier.PublicApi => Plugins.Abstractions.Options.ReductionLevel.PublicApi,
        RenderTier.Skeleton or RenderTier.Sketch => Plugins.Abstractions.Options.ReductionLevel.Skeleton,
        _ => Plugins.Abstractions.Options.ReductionLevel.Standard,
    };

    /// <summary>Maps a reduction level to the render tier stored on a context plan item.</summary>
    /// <param name="level">The reduction level from a fusion request.</param>
    /// <returns>The equivalent render tier.</returns>
    public static RenderTier FromReductionLevel(Plugins.Abstractions.Options.ReductionLevel level) => level switch
    {
        Plugins.Abstractions.Options.ReductionLevel.None => RenderTier.FullSource,
        Plugins.Abstractions.Options.ReductionLevel.Standard or Plugins.Abstractions.Options.ReductionLevel.Aggressive
            => RenderTier.Reduced,
        Plugins.Abstractions.Options.ReductionLevel.PublicApi => RenderTier.PublicApi,
        Plugins.Abstractions.Options.ReductionLevel.Skeleton => RenderTier.Skeleton,
        _ => RenderTier.Reduced,
    };
}
