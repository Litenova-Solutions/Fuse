using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Excludes files whose names exactly match configured exclusion names.
/// </summary>
public sealed class ExcludedFileNameFilter : IFileFilter
{
    /// <inheritdoc />
    public bool Include(FileCandidate candidate, CollectionOptions options)
    {
        if (options.ExcludeFiles.Count == 0)
            return true;

        return !options.ExcludeFiles.Contains(candidate.FileInfo.Name, StringComparer.OrdinalIgnoreCase);
    }
}
