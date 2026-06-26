namespace Fuse.Fusion.Indexing;

/// <summary>
///     A content-hash-keyed store of per-file <see cref="FileAnalysis" />, kept in the <c>analysis</c> namespace
///     of <c>.fuse/fuse.db</c> so repeated scoping calls in a session reuse analysis instead of recomputing it.
/// </summary>
/// <remarks>
///     Updated incrementally: a key is the hash of a file's content and analyzer tier, so an unchanged file is
///     a hit and a changed file is a miss that overwrites its entry. Implementations are thread-safe so the
///     graph build and relevance indexing can share one instance across a parallel run.
/// </remarks>
public interface IAnalysisIndex
{
    /// <summary>
    ///     Attempts to read a cached analysis for a key.
    /// </summary>
    /// <param name="key">The content-and-tier key produced by <see cref="AnalysisHasher" />.</param>
    /// <param name="analysis">The cached analysis when found; otherwise <see langword="null" />.</param>
    /// <returns><see langword="true" /> when an entry exists for <paramref name="key" />.</returns>
    bool TryGet(string key, out FileAnalysis? analysis);

    /// <summary>
    ///     Stores the analysis for a key, overwriting any existing entry.
    /// </summary>
    /// <param name="key">The content-and-tier key.</param>
    /// <param name="analysis">The analysis to store.</param>
    void Set(string key, FileAnalysis analysis);

    /// <summary>
    ///     Hit and miss counts for the run, for cold-versus-warm measurement.
    /// </summary>
    AnalysisIndexStatistics Statistics { get; }
}

/// <summary>
///     Mutable hit and miss counters for an <see cref="IAnalysisIndex" />.
/// </summary>
public sealed class AnalysisIndexStatistics
{
    private int _hits;
    private int _misses;

    /// <summary>The number of cache hits.</summary>
    public int Hits => _hits;

    /// <summary>The number of cache misses.</summary>
    public int Misses => _misses;

    /// <summary>Records a hit.</summary>
    public void RecordHit() => Interlocked.Increment(ref _hits);

    /// <summary>Records a miss.</summary>
    public void RecordMiss() => Interlocked.Increment(ref _misses);
}
