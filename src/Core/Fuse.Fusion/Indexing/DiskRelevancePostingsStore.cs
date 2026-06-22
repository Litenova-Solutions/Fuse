using System.Collections.Concurrent;

namespace Fuse.Fusion.Indexing;

/// <summary>
///     Disk-backed <see cref="IRelevancePostingsStore" /> storing one tab-separated token file per content
///     hash under <c>.fuse/index</c>, fronted by an in-memory map. Mirrors <see cref="DiskAnalysisIndex" />.
/// </summary>
/// <remarks>
///     Relevance tokens contain no tabs or newlines (the tokenizer emits lowercase alphanumeric stems), so the
///     tab-separated format needs no escaping. Reads and writes are resilient: a malformed or unreadable entry
///     is treated as a miss, and a write race (another process over the same index) is swallowed via an atomic
///     temp-file move, so a corrupted or contended entry degrades to recomputation.
/// </remarks>
public sealed class DiskRelevancePostingsStore : IRelevancePostingsStore
{
    private static readonly string[] Empty = [];

    private readonly string _directory;
    private readonly ConcurrentDictionary<ulong, IReadOnlyList<string>> _memory = new();

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiskRelevancePostingsStore" /> class for a source
    ///     directory.
    /// </summary>
    /// <param name="sourceDirectory">The source root under which <c>.fuse/index</c> is created.</param>
    public DiskRelevancePostingsStore(string sourceDirectory)
    {
        _directory = Path.Combine(
            Path.GetFullPath(sourceDirectory),
            DiskAnalysisIndex.IndexDirectoryName.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <inheritdoc />
    public bool TryGetBodyTokens(ulong contentHash, out IReadOnlyList<string> tokens)
    {
        if (_memory.TryGetValue(contentHash, out var cached))
        {
            tokens = cached;
            return true;
        }

        var path = EntryPath(contentHash);
        if (File.Exists(path))
        {
            try
            {
                var text = File.ReadAllText(path);
                tokens = text.Length == 0 ? Empty : text.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                _memory[contentHash] = tokens;
                return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        tokens = Empty;
        return false;
    }

    /// <inheritdoc />
    public void SetBodyTokens(ulong contentHash, IReadOnlyList<string> tokens)
    {
        _memory[contentHash] = tokens;

        try
        {
            Directory.CreateDirectory(_directory);
            var path = EntryPath(contentHash);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, string.Join('\t', tokens));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string EntryPath(ulong contentHash) => Path.Combine(_directory, $"bm25-{contentHash:x16}.idx");
}
