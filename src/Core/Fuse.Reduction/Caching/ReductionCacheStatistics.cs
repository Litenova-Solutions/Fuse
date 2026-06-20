namespace Fuse.Reduction.Caching;

/// <summary>
///     Tracks reduction cache hit and miss counts for a single fusion run.
/// </summary>
public sealed class ReductionCacheStatistics
{
    private int _hits;
    private int _misses;

    /// <summary>
    ///     Number of cache hits recorded so far.
    /// </summary>
    public int Hits => _hits;

    /// <summary>
    ///     Number of cache misses recorded so far.
    /// </summary>
    public int Misses => _misses;

    /// <summary>
    ///     Records a cache hit.
    /// </summary>
    /// <remarks>Thread-safe; the counter is incremented atomically.</remarks>
    public void RecordHit() => Interlocked.Increment(ref _hits);

    /// <summary>
    ///     Records a cache miss.
    /// </summary>
    /// <remarks>Thread-safe; the counter is incremented atomically.</remarks>
    public void RecordMiss() => Interlocked.Increment(ref _misses);
}
