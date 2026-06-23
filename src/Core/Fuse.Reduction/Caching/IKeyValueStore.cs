namespace Fuse.Reduction.Caching;

/// <summary>
///     Content-addressed key-value persistence shared by the cache and index views.
/// </summary>
/// <remarks>
///     Writes are buffered and committed in a single transaction at flush. Implementations must be safe for
///     concurrent readers and writers within one fusion run.
/// </remarks>
public interface IKeyValueStore : IAsyncDisposable
{
    /// <summary>
    ///     Reads a value for the namespaced key, consulting buffered writes first.
    /// </summary>
    /// <param name="store">The logical store namespace.</param>
    /// <param name="key">The entry key within <paramref name="store" />.</param>
    /// <param name="value">The value when found; otherwise <see langword="null" />.</param>
    /// <returns><see langword="true" /> when an entry exists for the key.</returns>
    bool TryGet(string store, string key, out byte[]? value);

    /// <summary>
    ///     Buffers a value for the next flush.
    /// </summary>
    /// <param name="store">The logical store namespace.</param>
    /// <param name="key">The entry key within <paramref name="store" />.</param>
    /// <param name="value">The value to store.</param>
    void Set(string store, string key, byte[] value);

    /// <summary>
    ///     Commits all buffered writes in one transaction.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the flush.</param>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes every entry under a logical store namespace.
    /// </summary>
    /// <param name="store">The logical store namespace to clear.</param>
    void Clear(string store);
}
