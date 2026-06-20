using System.Collections.Concurrent;

namespace Fuse.Fusion.Retrieval;

/// <summary>
///     Disk-backed <see cref="IVectorStore" /> writing one binary file per key under
///     <c>.fuse/index/vectors</c>, fronted by an in-memory map. Vectors are stored as little-endian
///     single-precision floats.
/// </summary>
/// <remarks>
///     Reads and writes are resilient: an unreadable or wrong-length entry is treated as a miss rather than
///     throwing, so a stale or corrupted vector degrades to recomputation.
/// </remarks>
public sealed class DiskVectorStore : IVectorStore
{
    /// <summary>The directory name, relative to the source root, where vectors are written.</summary>
    public const string VectorDirectoryName = ".fuse/index/vectors";

    private readonly string _directory;
    private readonly int _dimensions;
    private readonly ConcurrentDictionary<string, float[]> _memory = new(StringComparer.Ordinal);

    /// <summary>
    ///     Initializes a new instance of the <see cref="DiskVectorStore" /> class.
    /// </summary>
    /// <param name="sourceDirectory">The source root under which the vector directory is created.</param>
    /// <param name="dimensions">The expected vector length; entries of a different length are ignored.</param>
    public DiskVectorStore(string sourceDirectory, int dimensions)
    {
        _directory = Path.Combine(
            Path.GetFullPath(sourceDirectory),
            VectorDirectoryName.Replace('/', Path.DirectorySeparatorChar));
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

        var path = Path.Combine(_directory, key + ".vec");
        if (File.Exists(path))
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == _dimensions * sizeof(float))
                {
                    var loaded = new float[_dimensions];
                    Buffer.BlockCopy(bytes, 0, loaded, 0, bytes.Length);
                    _memory[key] = loaded;
                    vector = loaded;
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        vector = null;
        return false;
    }

    /// <inheritdoc />
    public void Set(string key, float[] vector)
    {
        _memory[key] = vector;

        try
        {
            Directory.CreateDirectory(_directory);
            var bytes = new byte[vector.Length * sizeof(float)];
            Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
            var path = Path.Combine(_directory, key + ".vec");
            // Write to a unique temp file then atomically replace, so a concurrent reader never observes a
            // partially written vector (and never accepts a torn but correct-length file).
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
