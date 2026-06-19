using Fuse.Collection.Models;

namespace Fuse.Collection.FileSystem;

/// <summary>
///     Provides cached source file content for a single fusion run.
/// </summary>
/// <remarks>
///     Caching is scoped to one run so the same file is read from disk at most once even when
///     multiple reduction or emission stages request its content. The primary implementation is
///     <see cref="SourceContentProvider" />.
/// </remarks>
public interface ISourceContentProvider
{
    /// <summary>
    ///     Gets file content, reading from disk on first access and caching for subsequent calls.
    /// </summary>
    /// <param name="file">The source file whose content is requested.</param>
    /// <param name="cancellationToken">A token to cancel the disk read on a cache miss.</param>
    /// <returns>
    ///     The full text content of <paramref name="file" />. Repeated calls for the same file
    ///     return the cached value without touching the file system.
    /// </returns>
    Task<string> GetContentAsync(SourceFile file, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Clears the in-run content cache.
    /// </summary>
    /// <remarks>
    ///     After this call the next <see cref="GetContentAsync" /> for any file reads from disk again.
    /// </remarks>
    void Clear();
}
