namespace Fuse.Reduction.Caching;

/// <summary>
///     Opens the per-source SQLite store used by reduction and persistent index adapters.
/// </summary>
public interface IFuseStoreFactory
{
    /// <summary>
    ///     Opens or creates the persistent SQLite store for a fusion source directory.
    /// </summary>
    /// <param name="sourceDirectory">The fusion source directory.</param>
    /// <returns>A key-value store that flushes on dispose.</returns>
    /// <remarks>
    ///     Inside a git repository the database is <c>{repoRoot}/.fuse/fuse.db</c>. Otherwise it is
    ///     <c>~/.fuse/fuse.db</c> (override with <see cref="FuseStorePaths.UserDataEnvironmentVariable" />).
    /// </remarks>
    /// <returns>A key-value store that flushes on dispose.</returns>
    IKeyValueStore Open(string sourceDirectory);
}
