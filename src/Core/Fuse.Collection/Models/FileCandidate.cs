namespace Fuse.Collection.Models;

/// <summary>
///     Represents a file discovered during directory enumeration before filtering completes.
/// </summary>
/// <remarks>
///     This type is an immutable data carrier with no behavioral logic.
///     Filters evaluate candidates against <see cref="Options.CollectionOptions" /> criteria.
/// </remarks>
public sealed record FileCandidate
{
    /// <summary>
    ///     Gets the absolute path to the file on disk.
    /// </summary>
    public string FullPath { get; }

    /// <summary>
    ///     Gets the path relative to the source directory root.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    ///     Gets metadata for the file, including size and timestamps.
    /// </summary>
    public FileInfo FileInfo { get; }

    /// <summary>
    ///     Initializes a new instance of the <see cref="FileCandidate" /> record.
    /// </summary>
    /// <param name="fullPath">The absolute path to the file.</param>
    /// <param name="relativePath">The path relative to the source directory.</param>
    /// <param name="fileInfo">Metadata for the file.</param>
    public FileCandidate(string fullPath, string relativePath, FileInfo fileInfo)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        FileInfo = fileInfo;
    }
}
