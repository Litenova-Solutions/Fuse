namespace Fuse.Fusion.Indexing;

/// <summary>
///     Persists the tokenized body of each file for the BM25 relevance index, keyed by content hash, so a
///     repeated scoped query against an unchanged tree skips re-tokenizing files it has already seen.
/// </summary>
/// <remarks>
///     Only the body field is cached: it is the one field whose tokenization scales with file size and is the
///     dominant cost of indexing. Symbol and path tokenization are cheap (and symbol analysis is already cached
///     by the analysis index), so they are recomputed each run. Keying by content hash makes invalidation
///     automatic: an edited file hashes differently, misses the cache, and is re-tokenized, while unchanged
///     files hit. The store is best-effort; a read or write failure degrades to recomputation.
/// </remarks>
public interface IRelevancePostingsStore
{
    /// <summary>
    ///     Attempts to retrieve the cached body tokens for a content hash.
    /// </summary>
    /// <param name="contentHash">The XxHash64 of the file's raw content.</param>
    /// <param name="tokens">The cached body tokens when present; otherwise an empty list.</param>
    /// <returns><see langword="true" /> on a cache hit; otherwise <see langword="false" />.</returns>
    bool TryGetBodyTokens(ulong contentHash, out IReadOnlyList<string> tokens);

    /// <summary>
    ///     Stores the body tokens for a content hash.
    /// </summary>
    /// <param name="contentHash">The XxHash64 of the file's raw content.</param>
    /// <param name="tokens">The body tokens to cache.</param>
    void SetBodyTokens(ulong contentHash, IReadOnlyList<string> tokens);
}
