using DotNet.Globbing;
using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Excludes files that match configured glob patterns by file name or relative path.
/// </summary>
public sealed class GlobPatternFilter : IFileFilter
{
    /// <inheritdoc />
    public bool Include(FileCandidate candidate, CollectionOptions options)
    {
        if (options.ExcludePatterns.Count == 0)
            return true;

        var normalizedRelativePath = candidate.RelativePath.Replace(Path.DirectorySeparatorChar, '/');

        foreach (var pattern in options.ExcludePatterns)
        {
            var glob = Glob.Parse(pattern);
            if (glob.IsMatch(candidate.FileInfo.Name) || glob.IsMatch(normalizedRelativePath))
                return false;
        }

        return true;
    }
}
