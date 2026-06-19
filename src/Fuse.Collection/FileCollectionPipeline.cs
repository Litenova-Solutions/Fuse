using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection;

/// <summary>
///     Enumerates and filters files from the file system based on collection options.
/// </summary>
/// <remarks>
///     Applies all registered <see cref="IFileFilter" /> implementations in registration order
///     using sequential enumeration. Results are sorted by descending file size.
/// </remarks>
public sealed class FileCollectionPipeline
{
    private readonly IFileSystem _fileSystem;
    private readonly GitIgnoreParser _gitIgnoreParser;
    private readonly IReadOnlyList<IFileFilter> _filters;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FileCollectionPipeline" /> class.
    /// </summary>
    /// <param name="fileSystem">The file system used to enumerate files.</param>
    /// <param name="gitIgnoreParser">The parser used to load <c>.gitignore</c> patterns.</param>
    /// <param name="filters">The filters to apply in registration order.</param>
    public FileCollectionPipeline(
        IFileSystem fileSystem,
        GitIgnoreParser gitIgnoreParser,
        IEnumerable<IFileFilter> filters)
    {
        _fileSystem = fileSystem;
        _gitIgnoreParser = gitIgnoreParser;
        _filters = filters.ToList();
    }

    /// <summary>
    ///     Collects all files matching the specified options.
    /// </summary>
    /// <param name="options">The collection options for the current run.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     A <see cref="CollectionResult" /> containing included files sorted by descending size
    ///     and the number of candidates evaluated.
    /// </returns>
    public async Task<CollectionResult> CollectAsync(
        CollectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var searchOption = options.Recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        var gitIgnorePatterns = options.RespectGitIgnore
            ? await _gitIgnoreParser.ParseAsync(options.SourceDirectory, cancellationToken)
            : [];

        foreach (var filter in _filters)
        {
            if (filter is GitIgnoreFilter gitIgnoreFilter)
                gitIgnoreFilter.SetPatterns(gitIgnorePatterns);
        }

        var candidatesEvaluated = 0;
        var includedFiles = new List<SourceFile>();

        foreach (var filePath in _fileSystem.EnumerateFiles(options.SourceDirectory, "*.*", searchOption))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidate = new FileCandidate(
                filePath,
                _fileSystem.GetRelativePath(options.SourceDirectory, filePath),
                _fileSystem.GetFileInfo(filePath));

            candidatesEvaluated++;

            if (_filters.All(filter => filter.Include(candidate, options)))
                includedFiles.Add(new SourceFile(candidate));
        }

        var sortedFiles = includedFiles
            .OrderByDescending(file => file.FileInfo.Length)
            .ToList();

        return new CollectionResult(sortedFiles, candidatesEvaluated);
    }
}
