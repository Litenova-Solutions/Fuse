using Fuse.Collection.Models;

namespace Fuse.Collection.FileSystem;

/// <summary>
///     Provides cached source file content for a single fusion run.
/// </summary>
public interface ISourceContentProvider
{
    /// <summary>
    ///     Gets file content, reading from disk on first access and caching for subsequent calls.
    /// </summary>
    Task<string> GetContentAsync(SourceFile file, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Clears the in-run content cache.
    /// </summary>
    void Clear();
}
