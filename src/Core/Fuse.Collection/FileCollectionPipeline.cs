using System.Collections.Concurrent;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection;

/// <summary>
///     Enumerates and filters files from the file system based on collection options.
/// </summary>
/// <remarks>
///     Applies all registered <see cref="IFileFilter" /> implementations in registration order.
///     Results are sorted by normalized relative path for deterministic ordering.
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
    ///     Collects all files matching the specified options using the processor count for parallelism.
    /// </summary>
    /// <param name="options">The collection options for the current run.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     A <see cref="CollectionResult" /> containing included files sorted by path
    ///     and the number of candidates evaluated.
    /// </returns>
    public Task<CollectionResult> CollectAsync(
        CollectionOptions options,
        CancellationToken cancellationToken = default) =>
        CollectAsync(options, Environment.ProcessorCount, cancellationToken);

    /// <summary>
    ///     Collects all files matching the specified options.
    /// </summary>
    /// <param name="options">The collection options for the current run.</param>
    /// <param name="parallelism">The maximum degree of parallelism for candidate evaluation.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     A <see cref="CollectionResult" /> containing included files sorted by path
    ///     and the number of candidates evaluated.
    /// </returns>
    public async Task<CollectionResult> CollectAsync(
        CollectionOptions options,
        int parallelism,
        CancellationToken cancellationToken = default)
    {
        // Explicit-file mode (the reduce path): collect exactly the caller-supplied paths and skip enumeration
        // and filters. The caller chose these files deliberately, so include/exclude rules do not apply.
        if (options.ExplicitFiles is { Count: > 0 })
            return CollectExplicitFiles(options);

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

        var filePaths = _fileSystem
            .EnumerateFiles(options.SourceDirectory, "*.*", searchOption)
            .Select((path, index) => (path, index))
            .ToArray();

        var includedFiles = new ConcurrentBag<(int Index, SourceFile File)>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken
        };

        await Parallel.ForEachAsync(filePaths, parallelOptions, (item, _) =>
        {
            var candidate = new FileCandidate(
                item.path,
                _fileSystem.GetRelativePath(options.SourceDirectory, item.path),
                _fileSystem.GetFileInfo(item.path));

            if (_filters.All(filter => filter.Include(candidate, options)))
                includedFiles.Add((item.index, new SourceFile(candidate)));

            return ValueTask.CompletedTask;
        });

        var sortedFiles = includedFiles
            .OrderBy(entry => entry.Index)
            .Select(entry => entry.File)
            .OrderBy(file => file.NormalizedRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // (explicit-file mode returns earlier; see CollectExplicitFiles)

        return new CollectionResult(sortedFiles, filePaths.Length);
    }

    // Builds source files from a caller-supplied path list, bypassing directory enumeration and the filters.
    // Paths are resolved relative to SourceDirectory when not rooted; missing paths are skipped so a stale
    // entry degrades the set rather than failing the run. Input order is preserved so the caller controls
    // emission order.
    private CollectionResult CollectExplicitFiles(CollectionOptions options)
    {
        var files = new List<SourceFile>();
        var candidateCount = 0;

        foreach (var raw in options.ExplicitFiles!)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            candidateCount++;
            var fullPath = Path.IsPathRooted(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(options.SourceDirectory, raw));

            var fileInfo = _fileSystem.GetFileInfo(fullPath);
            if (!fileInfo.Exists)
                continue;

            var relativePath = _fileSystem.GetRelativePath(options.SourceDirectory, fullPath);
            files.Add(new SourceFile(new FileCandidate(fullPath, relativePath, fileInfo)));
        }

        return new CollectionResult(files, candidateCount);
    }
}
