namespace Fuse.Reduction.Caching;

/// <summary>
///     Stores reduction results on disk under <c>.fuse/cache/</c> keyed by XXHash64 hashes.
/// </summary>
public sealed class DiskReductionCache : IReductionCache
{
    /// <summary>
    ///     The relative cache directory name under the source root.
    /// </summary>
    public const string CacheDirectoryName = ".fuse/cache";

    private readonly string _cacheDirectory;
    private readonly object _sync = new();

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiskReductionCache" /> class.
    /// </summary>
    /// <param name="sourceDirectory">The project root used to resolve <c>.fuse/cache/</c>.</param>
    public DiskReductionCache(string sourceDirectory)
    {
        _cacheDirectory = Path.Combine(
            Path.GetFullPath(sourceDirectory),
            CacheDirectoryName.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <inheritdoc />
    public ReductionCacheStatistics Statistics { get; } = new();

    /// <inheritdoc />
    public bool TryGet(ulong contentHash, ulong reductionOptionsHash, out string reducedContent)
    {
        lock (_sync)
        {
            var path = GetEntryPath(contentHash, reductionOptionsHash);
            if (!File.Exists(path))
            {
                Statistics.RecordMiss();
                reducedContent = string.Empty;
                return false;
            }

            reducedContent = File.ReadAllText(path);
            Statistics.RecordHit();
            return true;
        }
    }

    /// <inheritdoc />
    public void Set(ulong contentHash, ulong reductionOptionsHash, string reducedContent)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(_cacheDirectory);
            var path = GetEntryPath(contentHash, reductionOptionsHash);
            File.WriteAllText(path, reducedContent);
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_sync)
        {
            if (!Directory.Exists(_cacheDirectory))
                return;

            foreach (var file in Directory.EnumerateFiles(_cacheDirectory))
            {
                File.Delete(file);
            }
        }
    }

    private string GetEntryPath(ulong contentHash, ulong reductionOptionsHash) =>
        Path.Combine(_cacheDirectory, ReductionHasher.FormatCacheFileName(contentHash, reductionOptionsHash));
}
