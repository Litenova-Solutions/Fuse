using DotNet.Globbing;
using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Excludes files that match <c>.gitignore</c> patterns parsed from the source directory tree.
/// </summary>
/// <remarks>
///     The pattern set is fixed at construction and never mutated, so a single instance is safe to share or
///     to evaluate concurrently. <see cref="FileCollectionPipeline" /> builds a fresh instance carrying the
///     current run's patterns rather than mutating a shared one, which keeps concurrent collection runs against
///     different repositories from racing on a process-wide pattern set.
/// </remarks>
public sealed class GitIgnoreFilter : IFileFilter
{
    private readonly IReadOnlyList<Glob> _patterns;

    /// <summary>
    ///     Initializes a filter with no patterns, which excludes nothing. Used as the registered placeholder
    ///     that <see cref="FileCollectionPipeline" /> replaces per run with a pattern-carrying instance.
    /// </summary>
    public GitIgnoreFilter() : this([])
    {
    }

    /// <summary>
    ///     Initializes a filter with the compiled <c>.gitignore</c> patterns for a collection run.
    /// </summary>
    /// <param name="patterns">The glob patterns to apply, typically produced by <see cref="Fuse.Collection.FileSystem.GitIgnoreParser" />.</param>
    public GitIgnoreFilter(IReadOnlyList<Glob> patterns)
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
