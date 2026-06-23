using System.Collections.Concurrent;
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

    private readonly IKeyValueStore _store;
    private readonly int _dimensions;
    private readonly ConcurrentDictionary<string, float[]> _memory = new(StringComparer.Ordinal);

    /// <summary>
    ///     Initializes a new instance of the <see cref="SqliteVectorStore" /> class.
    /// </summary>
    /// <param name="store">The shared key-value store for the run.</param>
    /// <param name="dimensions">The expected vector length; entries of a different length are ignored.</param>
    public SqliteVectorStore(IKeyValueStore store, int dimensions)
    {
        _store = store;
        _dimensions = dimensions;
    }

    /// <inheritdoc />
    public bool TryGet(string key, out float[]? vector)
    {
        if (_memory.TryGetValue(key, out var cached))
        {
            vector = cached;
            return true;
        }

        if (_store.TryGet(StoreName, key, out var bytes) && bytes is not null)
        {
            if (bytes.Length == _dimensions * sizeof(float))
            {
                var loaded = new float[_dimensions];
                Buffer.BlockCopy(bytes, 0, loaded, 0, bytes.Length);
                _memory[key] = loaded;
                vector = loaded;
                return true;
            }
        }

        vector = null;
        return false;
    }

    /// <inheritdoc />
    public void Set(string key, float[] vector)
    {
        _memory[key] = vector;
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        _store.Set(StoreName, key, bytes);
    }
}
