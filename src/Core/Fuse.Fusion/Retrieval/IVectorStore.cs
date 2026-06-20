namespace Fuse.Fusion.Retrieval;

/// <summary>
///     An on-disk store of embedding vectors keyed by content hash, so a candidate file embedded once in a
///     session is not re-embedded on a later call.
/// </summary>
public interface IVectorStore
{
    /// <summary>
    ///     Reads a cached vector for a key.
    /// </summary>
    /// <param name="key">The content-and-model key.</param>
    /// <param name="vector">The cached vector when present; otherwise <see langword="null" />.</param>
    /// <returns><see langword="true" /> when a vector is found.</returns>
    bool TryGet(string key, out float[]? vector);

    /// <summary>
    ///     Stores a vector for a key.
    /// </summary>
    /// <param name="key">The content-and-model key.</param>
    /// <param name="vector">The vector to store.</param>
    void Set(string key, float[] vector);
}
