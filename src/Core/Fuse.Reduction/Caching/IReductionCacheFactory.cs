namespace Fuse.Reduction.Caching;

/// <summary>
///     Creates reduction cache instances for a fusion run.
/// </summary>
public interface IReductionCacheFactory
{
    /// <summary>
    ///     Creates a reduction cache for the specified source directory.
    /// </summary>
    /// <param name="sourceDirectory">The project root directory.</param>
    /// <param name="enabled">Whether caching is enabled.</param>
    /// <param name="clearBeforeRun">Whether to clear existing cache entries before the run.</param>
    /// <returns>
    ///     A disk-backed cache when <paramref name="enabled" /> is <c>true</c>; otherwise a no-op cache
    ///     that never stores or returns entries.
    /// </returns>
    IReductionCache Create(string sourceDirectory, bool enabled, bool clearBeforeRun);
}
