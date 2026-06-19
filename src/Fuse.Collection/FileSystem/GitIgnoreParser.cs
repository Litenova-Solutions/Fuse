using DotNet.Globbing;

namespace Fuse.Collection.FileSystem;

/// <summary>
///     Parses <c>.gitignore</c> files and provides glob patterns for file exclusion.
/// </summary>
/// <remarks>
///     Walks up the directory tree from a starting directory, collecting all
///     <c>.gitignore</c> patterns found along the way. Traversal stops at the
///     repository root (directory containing a <c>.git</c> folder) or the file system root.
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
        var currentDirectory = startDirectory;

        while (!string.IsNullOrEmpty(currentDirectory))
        {
            var gitIgnorePath = Path.Combine(currentDirectory, ".gitignore");
            if (_fileSystem.GetFileInfo(gitIgnorePath).Exists)
            {
                var content = await _fileSystem.ReadAllTextAsync(gitIgnorePath, cancellationToken);
                var lines = content.Split('\n');
                foreach (var line in lines)
                {
                    var trimmedLine = line.Trim();
                    if (!string.IsNullOrEmpty(trimmedLine) && !trimmedLine.StartsWith('#'))
                    {
                        var globPattern = Path.Combine(currentDirectory, trimmedLine)
                            .Replace(Path.DirectorySeparatorChar, '/');

                        patterns.Add(Glob.Parse(globPattern));
                    }
                }
            }

            var parent = Directory.GetParent(currentDirectory);
            if (parent == null || parent.GetDirectories(".git").Length > 0)
                break;

            currentDirectory = parent.FullName;
        }

        return patterns;
    }
}
