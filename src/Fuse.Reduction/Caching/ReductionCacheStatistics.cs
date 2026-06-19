namespace Fuse.Reduction.Caching;

/// <summary>
///     Tracks reduction cache hit and miss counts for a single fusion run.
/// </summary>
public sealed class ReductionCacheStatistics
{
    private int _hits;
    private int _misses;

    /// <summary>
    ///     Gets the number of cache hits.
    /// </summary>
    public int Hits => _hits;

    /// <summary>
    ///     Gets the number of cache misses.
    /// </summary>
    public int Misses => _misses;

    /// <summary>
    ///     Records a cache hit.
    /// </summary>
    public void RecordHit() => Interlocked.Increment(ref _hits);

    /// <summary>
    ///     Records a cache miss.
    /// </summary>
    public void RecordMiss() => Interlocked.Increment(ref _misses);
}
