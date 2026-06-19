namespace Fuse.Collection.FileSystem;

/// <summary>
///     Defines an abstraction for file system operations used during file collection.
/// </summary>
/// <remarks>
///     This interface provides a testable abstraction over <see cref="System.IO" /> operations.
///     The primary implementation is <see cref="PhysicalFileSystem" />.
/// </remarks>
public interface IFileSystem
{
    /// <summary>
    ///     Determines whether the specified directory exists.
    /// </summary>
    /// <param name="path">The path to the directory to check.</param>
    /// <returns><c>true</c> if the directory exists; otherwise, <c>false</c>.</returns>
    bool DirectoryExists(string path);

    /// <summary>
    ///     Creates all directories and subdirectories in the specified path.
    /// </summary>
    /// <param name="path">The directory path to create.</param>
    void CreateDirectory(string path);

    /// <summary>
    ///     Returns an enumerable collection of file paths matching a search pattern.
    /// </summary>
    /// <param name="path">The path to the directory to search.</param>
    /// <param name="searchPattern">The search pattern to match file names.</param>
    /// <param name="searchOption">Whether to search subdirectories.</param>
    /// <returns>An enumerable collection of full file paths.</returns>
    IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption);

    /// <summary>
    ///     Asynchronously reads all text from a file.
    /// </summary>
    /// <param name="path">The path to the file to read.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The full text content of the file.</returns>
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Asynchronously writes all text to a file, creating it or overwriting any existing content.
    /// </summary>
    /// <param name="path">The path to the file to write.</param>
    /// <param name="contents">The text to write to the file.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets information about a file.
    /// </summary>
    /// <param name="path">The path to the file.</param>
    /// <returns>
    ///     A <see cref="FileInfo" /> object describing the path. The object is returned even when the
    ///     file does not exist; inspect <see cref="FileInfo.Exists" /> to confirm presence.
    /// </returns>
    FileInfo GetFileInfo(string path);

    /// <summary>
    ///     Determines whether a file is binary by inspecting its raw byte content.
    /// </summary>
    /// <param name="filePath">The path to the file to check.</param>
    /// <returns><c>true</c> if the file appears to be binary; otherwise, <c>false</c>.</returns>
    bool IsBinaryFile(string filePath);

    /// <summary>
    ///     Gets the relative path from one path to another.
    /// </summary>
    /// <param name="relativeTo">The source path to calculate the relative path from.</param>
    /// <param name="path">The target path.</param>
    /// <returns>The relative path from <paramref name="relativeTo" /> to <paramref name="path" />.</returns>
    string GetRelativePath(string relativeTo, string path);
}
