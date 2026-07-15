namespace Fuse.Indexing;

/// <summary>
///     Raised when a full-text search is issued against a store whose <c>chunk_fts</c> table is absent (the FTS
///     stamp and the actual table disagree). The search path never lets the raw SQLite
///     <c>no such table: chunk_fts</c> error escape as an opaque internal error (R23); callers map this to the
///     stable <c>index_rebuilding:</c> operational prefix and trigger a rebuild.
/// </summary>
public sealed class SearchIndexUnavailableException : Exception
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="SearchIndexUnavailableException" /> class.
    /// </summary>
    /// <param name="message">The human-readable reason the search index is unavailable.</param>
    public SearchIndexUnavailableException(string message)
        : base(message)
    {
    }
}
