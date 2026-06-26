using DotNet.Globbing;

namespace Fuse.Collection.FileSystem;

/// <summary>
///     Parses <c>.gitignore</c> files and provides glob patterns for file exclusion.
/// </summary>
/// <remarks>
///     Walks up the directory tree from a starting directory, collecting all
///     <c>.gitignore</c> patterns found along the way. Traversal stops at the
///     git repository root resolved by <see cref="RepositoryRootResolver.TryFindRepositoryRoot" />
///     (inclusive) or the file system root when not inside a repository.
/// </remarks>
public sealed class GitIgnoreParser
{
    private readonly IFileSystem _fileSystem;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GitIgnoreParser" /> class.
    /// </summary>
    /// <param name="fileSystem">The file system implementation to use for file operations.</param>
    public GitIgnoreParser(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <summary>
    ///     Asynchronously parses all <c>.gitignore</c> files from the starting directory up to the repository root.
    /// </summary>
    /// <param name="startDirectory">The directory to start parsing from.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    ///     A list of compiled glob patterns that can be matched against absolute file paths.
    ///     Returns an empty list when no <c>.gitignore</c> files are found.
    /// </returns>
    public async Task<IReadOnlyList<Glob>> ParseAsync(string startDirectory, CancellationToken cancellationToken = default)
    {
        var patterns = new List<Glob>();
        var currentDirectory = Path.GetFullPath(startDirectory);
        var repositoryRoot = RepositoryRootResolver.TryFindRepositoryRoot(currentDirectory);

        while (!string.IsNullOrEmpty(currentDirectory))
        {
            var gitIgnorePath = Path.Combine(currentDirectory, ".gitignore");
            if (_fileSystem.GetFileInfo(gitIgnorePath).Exists)
            {
                var content = await _fileSystem.ReadAllTextAsync(gitIgnorePath, cancellationToken);
                var remaining = content.AsSpan();
                while (!remaining.IsEmpty)
                {
                    var newlineIndex = remaining.IndexOf('\n');
                    var lineSpan = newlineIndex >= 0 ? remaining[..newlineIndex] : remaining;
                    remaining = newlineIndex >= 0 ? remaining[(newlineIndex + 1)..] : ReadOnlySpan<char>.Empty;
                    var trimmedLine = lineSpan.TrimEnd('\r').ToString().Trim();
                    if (!string.IsNullOrEmpty(trimmedLine) && !trimmedLine.StartsWith('#'))
                    {
                        var globPattern = Path.Combine(currentDirectory, trimmedLine)
                            .Replace(Path.DirectorySeparatorChar, '/');

                        patterns.Add(Glob.Parse(globPattern));
                    }
                }
            }

            if (repositoryRoot is not null &&
                string.Equals(currentDirectory, repositoryRoot, StringComparison.OrdinalIgnoreCase))
                break;

            var parent = Directory.GetParent(currentDirectory);
            if (parent is null)
                break;

            currentDirectory = parent.FullName;
        }

        return patterns;
    }
}
