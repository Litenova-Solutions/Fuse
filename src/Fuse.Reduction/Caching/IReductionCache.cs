namespace Fuse.Reduction.Caching;

/// <summary>
///     Caches per-file reduction results keyed by content and options hashes.
/// </summary>
public interface IReductionCache
{
    /// <summary>
    ///     Gets cache statistics for the current run.
    /// </summary>
    ReductionCacheStatistics Statistics { get; }

    /// <summary>
    ///     Attempts to retrieve a cached reduction result.
    /// </summary>
    /// <param name="contentHash">Hash of the raw file content.</param>
    /// <param name="reductionOptionsHash">Hash of reduction options and file extension.</param>
    /// <param name="reducedContent">The cached reduced content when found.</param>
    /// <returns><c>true</c> when a cache entry exists; otherwise, <c>false</c>.</returns>
    bool TryGet(ulong contentHash, ulong reductionOptionsHash, out string reducedContent);

    /// <summary>
    ///     Stores a reduction result in the cache.
    /// </summary>
    void Set(ulong contentHash, ulong reductionOptionsHash, string reducedContent);

    /// <summary>
    ///     Removes all cached entries.
    /// </summary>
    void Clear();
}
