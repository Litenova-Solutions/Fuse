namespace Fuse.Reduction.Caching;

/// <summary>
///     A no-op reduction cache used when caching is disabled.
/// </summary>
public sealed class NullReductionCache : IReductionCache
{
    /// <summary>
    ///     Gets the shared null cache instance.
    /// </summary>
    public static NullReductionCache Instance { get; } = new();

    /// <inheritdoc />
    public ReductionCacheStatistics Statistics { get; } = new();

    /// <inheritdoc />
    public bool TryGet(ulong contentHash, ulong reductionOptionsHash, out string reducedContent)
    {
        reducedContent = string.Empty;
        return false;
    }

    /// <inheritdoc />
    public void Set(ulong contentHash, ulong reductionOptionsHash, string reducedContent)
    {
    }

    /// <inheritdoc />
    public void Clear()
    {
    }
}
