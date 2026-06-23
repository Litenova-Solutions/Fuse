using System.Collections.Concurrent;

namespace Fuse.Reduction.Caching;

/// <summary>
///     Default factory for SQLite-backed fuse stores.
/// </summary>
public sealed class FuseStoreFactory : IFuseStoreFactory
{
    /// <summary>The database path relative to the source root.</summary>
    public const string DatabaseRelativePath = ".fuse/fuse.db";

    private static readonly ConcurrentDictionary<string, byte> Migrated = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IKeyValueStore Open(string sourceDirectory)
    {
        var root = Path.GetFullPath(sourceDirectory);
        TryMigrateLegacyDirectories(root);
        var databasePath = Path.Combine(
            root,
            DatabaseRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return new SqliteKeyValueStore(databasePath);
    }

    private static void TryMigrateLegacyDirectories(string root)
    {
        if (!Migrated.TryAdd(root, 0))
            return;

        foreach (var legacy in new[] { ".fuse/cache", ".fuse/index" })
        {
            var path = Path.Combine(root, legacy.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
