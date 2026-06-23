using Fuse.Fusion.Storage;
using Fuse.Reduction.Caching;

namespace Fuse.Fusion.Retrieval;

/// <summary>
///     SQLite-backed <see cref="IVectorStore" /> using the <c>vectors</c> store namespace, fronted by an
///     in-memory map. Vectors are stored as little-endian single-precision floats.
/// </summary>
/// <remarks>
///     An unreadable or wrong-length entry is treated as a miss rather than throwing.
/// </remarks>
public sealed class SqliteVectorStore : IVectorStore
{
    private const string StoreName = "vectors";

    private readonly NamespacedKvCache<string, float[]> _cache;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SqliteVectorStore" /> class.
    /// </summary>
    /// <param name="store">The shared key-value store for the run.</param>
    /// <param name="dimensions">The expected vector length; entries of a different length are ignored.</param>
    public SqliteVectorStore(IKeyValueStore store, int dimensions) =>
        _cache = new NamespacedKvCache<string, float[]>(
            store,
            StoreName,
            static key => key,
            NamespacedKvCodec.EncodeVector,
            bytes => NamespacedKvCodec.DecodeVector(bytes, dimensions),
            StringComparer.Ordinal);

    /// <inheritdoc />
    public bool TryGet(string key, out float[]? vector) => _cache.TryGet(key, out vector);

    /// <inheritdoc />
    public void Set(string key, float[] vector) => _cache.Set(key, vector);
}
