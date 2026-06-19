using DotNet.Globbing;
using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Excludes files that match <c>.gitignore</c> patterns parsed from the source directory tree.
/// </summary>
public sealed class GitIgnoreFilter : IFileFilter
{
    private IReadOnlyList<Glob> _patterns = [];

    /// <summary>
    ///     Sets the compiled <c>.gitignore</c> patterns for the current collection run.
    /// </summary>
    /// <param name="patterns">The glob patterns to apply.</param>
    public void SetPatterns(IReadOnlyList<Glob> patterns)
    {
        _patterns = patterns;
    }

    /// <inheritdoc />
    public bool Include(FileCandidate candidate, CollectionOptions options)
    {
        if (!options.RespectGitIgnore)
            return true;

        var normalizedPath = candidate.FullPath.Replace(Path.DirectorySeparatorChar, '/');
        return !_patterns.Any(pattern => pattern.IsMatch(normalizedPath));
    }
}
