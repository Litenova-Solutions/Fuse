using System.Collections.Concurrent;
using System.Text;
using Fuse.Reduction.Caching;

namespace Fuse.Fusion.Indexing;

/// <summary>
///     SQLite-backed <see cref="IRelevancePostingsStore" /> using the <c>postings</c> store namespace,
///     fronted by an in-memory map.
/// </summary>
/// <remarks>
///     Relevance tokens contain no tabs or newlines, so the tab-separated format needs no escaping. Malformed
///     or unreadable entries are treated as misses.
/// </remarks>
public sealed class SqliteRelevancePostingsStore : IRelevancePostingsStore
{
    private const string StoreName = "postings";

    private static readonly string[] Empty = [];

    private readonly IKeyValueStore _store;
    private readonly ConcurrentDictionary<ulong, IReadOnlyList<string>> _memory = new();

    /// <summary>
    ///     Initializes a new instance of the <see cref="SqliteRelevancePostingsStore" /> class.
    /// </summary>
    /// <param name="store">The shared key-value store for the run.</param>
    public SqliteRelevancePostingsStore(IKeyValueStore store) => _store = store;

    /// <inheritdoc />
    public bool TryGetBodyTokens(ulong contentHash, out IReadOnlyList<string> tokens)
    {
        if (_memory.TryGetValue(contentHash, out var cached))
        {
            tokens = cached;
            return true;
        }

        var key = Key(contentHash);
        if (_store.TryGet(StoreName, key, out var bytes) && bytes is not null)
        {
            try
            {
                var text = Encoding.UTF8.GetString(bytes);
                tokens = text.Length == 0 ? Empty : text.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                _memory[contentHash] = tokens;
                return true;
            }
            catch (DecoderFallbackException)
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
        _store.Set(StoreName, Key(contentHash), Encoding.UTF8.GetBytes(string.Join('\t', tokens)));
    }

    private static string Key(ulong contentHash) => $"{contentHash:x16}";
}
