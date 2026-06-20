namespace Fuse.Reduction.Caching;

/// <summary>
///     Default factory for disk-backed reduction caches.
/// </summary>
public sealed class ReductionCacheFactory : IReductionCacheFactory
{
    /// <inheritdoc />
    public IReductionCache Create(string sourceDirectory, bool enabled, bool clearBeforeRun)
    {
        if (!enabled)
            return NullReductionCache.Instance;

        var cache = new DiskReductionCache(sourceDirectory);
        if (clearBeforeRun)
            cache.Clear();

        return cache;
    }
}
