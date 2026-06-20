namespace Fuse.Reduction.Caching;

/// <summary>
///     Caches per-file reduction results keyed by content and options hashes.
/// </summary>
public interface IReductionCache
{
    /// <summary>
    ///     Hit and miss counters accumulated over the current run.
    /// </summary>
    ReductionCacheStatistics Statistics { get; }

    /// <summary>
    ///     Attempts to retrieve a cached reduction result.
    /// </summary>
    /// <param name="contentHash">Hash of the raw file content.</param>
    /// <param name="reductionOptionsHash">Hash of reduction options and file extension.</param>
    /// <param name="reducedContent">The cached reduced content when found; otherwise <see cref="string.Empty" />.</param>
    /// <returns><c>true</c> when a cache entry exists; otherwise, <c>false</c>.</returns>
    /// <remarks>Records a hit or miss on <see cref="Statistics" /> as a side effect of each lookup.</remarks>
    bool TryGet(ulong contentHash, ulong reductionOptionsHash, out string reducedContent);

    /// <summary>
    ///     Stores a reduction result in the cache, overwriting any existing entry for the same key.
    /// </summary>
    /// <param name="contentHash">Hash of the raw file content.</param>
    /// <param name="reductionOptionsHash">Hash of reduction options and file extension.</param>
    /// <param name="reducedContent">The reduced content to cache.</param>
    void Set(ulong contentHash, ulong reductionOptionsHash, string reducedContent);

    /// <summary>
    ///     Removes all cached entries.
    /// </summary>
    void Clear();
}
