namespace Fuse.Reduction.Caching;

/// <summary>
///     Opens the per-source derived key-value cache used by reduction and analysis-index adapters.
/// </summary>
public interface IFuseStoreFactory
{
    /// <summary>
    ///     Opens or creates the derived key-value cache for a fusion source directory.
    /// </summary>
    /// <param name="sourceDirectory">The fusion source directory.</param>
    /// <returns>A key-value store that flushes on dispose.</returns>
    /// <remarks>
    ///     Inside a git repository the database is <c>{repoRoot}/.fuse/fuse-cache.db</c>. Otherwise it is
    ///     <c>~/.fuse/fuse-cache.db</c> (override with <see cref="FuseStorePaths.UserDataEnvironmentVariable" />).
    /// </remarks>
    IKeyValueStore Open(string sourceDirectory);
}
