using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Excludes zero-byte files when <see cref="CollectionOptions.ExcludeEmptyFiles" /> is enabled.
/// </summary>
public sealed class EmptyFileFilter : IFileFilter
{
    /// <inheritdoc />
    public bool Include(FileCandidate candidate, CollectionOptions options)
    {
        if (!options.ExcludeEmptyFiles)
            return true;

        return candidate.FileInfo.Length > 0;
    }
}
