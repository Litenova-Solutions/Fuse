namespace Fuse.Reduction.Caching;

/// <summary>
///     Opens the per-source SQLite store used by reduction and persistent index adapters.
/// </summary>
public interface IFuseStoreFactory
{
    /// <summary>
    ///     Opens or creates <c>.fuse/fuse.db</c> under the source directory.
    /// </summary>
    /// <param name="sourceDirectory">The project root directory.</param>
    /// <returns>A key-value store that flushes on dispose.</returns>
    /// <remarks>
    ///     On first open for a source directory in this process, legacy <c>.fuse/cache</c> and
    ///     <c>.fuse/index</c> directories are best-effort deleted.
    /// </remarks>
    IKeyValueStore Open(string sourceDirectory);
}
