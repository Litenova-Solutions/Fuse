namespace Fuse.Reduction.Caching;

/// <summary>
///     Default factory for SQLite-backed fuse stores.
/// </summary>
public sealed class FuseStoreFactory : IFuseStoreFactory
{
    /// <inheritdoc />
    public IKeyValueStore Open(string sourceDirectory) =>
        new SqliteKeyValueStore(FuseStorePaths.ResolveDatabasePath(sourceDirectory));
}
