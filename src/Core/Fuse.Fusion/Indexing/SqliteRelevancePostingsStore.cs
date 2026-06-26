using Fuse.Fusion.Storage;
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

    private readonly NamespacedKvCache<ulong, IReadOnlyList<string>> _cache;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SqliteRelevancePostingsStore" /> class.
    /// </summary>
    /// <param name="store">The shared key-value store for the run.</param>
    public SqliteRelevancePostingsStore(IKeyValueStore store) =>
        _cache = new NamespacedKvCache<ulong, IReadOnlyList<string>>(
            store,
            StoreName,
            NamespacedKvCodec.HashKey,
            NamespacedKvCodec.EncodeTokens,
            NamespacedKvCodec.DecodeTokens);

    /// <inheritdoc />
    public bool TryGetBodyTokens(ulong contentHash, out IReadOnlyList<string> tokens)
    {
        if (_cache.TryGet(contentHash, out var cached))
        {
            tokens = cached!;
            return true;
        }

        tokens = NamespacedKvCodec.EmptyTokens;
        return false;
    }

    /// <inheritdoc />
    public void SetBodyTokens(ulong contentHash, IReadOnlyList<string> tokens) =>
        _cache.Set(contentHash, tokens);
}
