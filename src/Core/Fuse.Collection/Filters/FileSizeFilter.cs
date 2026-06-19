using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Excludes files that exceed the configured maximum file size.
/// </summary>
public sealed class FileSizeFilter : IFileFilter
{
    /// <inheritdoc />
    public bool Include(FileCandidate candidate, CollectionOptions options)
    {
        if (options.MaxFileSizeKb == 0)
            return true;

        return candidate.FileInfo.Length <= options.MaxFileSizeKb * 1024L;
    }
}
