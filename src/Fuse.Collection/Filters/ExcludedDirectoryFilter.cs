using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Excludes files located within configured excluded directory names.
/// </summary>
public sealed class ExcludedDirectoryFilter : IFileFilter
{
    /// <inheritdoc />
    public bool Include(FileCandidate candidate, CollectionOptions options)
    {
        if (options.ExcludeDirectories.Count == 0)
            return true;

        var pathParts = candidate.RelativePath
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        return !pathParts.Any(part =>
            options.ExcludeDirectories.Contains(part, StringComparer.OrdinalIgnoreCase));
    }
}
