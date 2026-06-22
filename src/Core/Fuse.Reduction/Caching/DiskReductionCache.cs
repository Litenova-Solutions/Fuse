namespace Fuse.Reduction.Caching;

/// <summary>
///     Stores reduction results on disk under <c>.fuse/cache/</c> keyed by XXHash64 hashes.
/// </summary>
/// <remarks>
///     Operations are serialized under an internal lock for the parallel reduction workers of a single run.
///     Across concurrent runs (which each hold their own cache instance over the same directory) safety comes
///     from writing each entry to a unique temp file and atomically moving it into place, and from treating a
///     read or write that races another run as a miss: the cache is best-effort and every entry for a given
///     key is byte-identical, so a lost write costs only a recomputation. Each entry is a single file named
///     from the content and options hashes; the cache directory is created lazily on first <see cref="Set" />.
/// </remarks>
/// <seealso cref="IReductionCache" />
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

            try
            {
                reducedContent = File.ReadAllText(path);
            }
            catch (IOException)
            {
                // A concurrent run is mid-move on this entry; treat as a miss and recompute.
                Statistics.RecordMiss();
                reducedContent = string.Empty;
                return false;
            }

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

            // Write to a unique temp file then atomically move it into place, so a concurrent run reading or
            // writing the same key never sees a partial file. A race on the move is swallowed: the entry is
            // byte-identical regardless of which run wins, so a lost write only forces a recomputation.
            var temp = path + '.' + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, reducedContent);
                File.Move(temp, path, overwrite: true);
            }
            catch (IOException)
            {
                TryDelete(temp);
            }
            catch (UnauthorizedAccessException)
            {
                TryDelete(temp);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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
