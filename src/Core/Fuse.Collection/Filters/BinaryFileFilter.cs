using Fuse.Collection.FileSystem;
using Fuse.Collection.Models;
using Fuse.Collection.Options;

namespace Fuse.Collection.Filters;

/// <summary>
///     Excludes binary files when <see cref="CollectionOptions.IgnoreBinaryFiles" /> is enabled.
/// </summary>
public sealed class BinaryFileFilter : IFileFilter
{
    private readonly IFileSystem _fileSystem;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BinaryFileFilter" /> class.
    /// </summary>
    /// <param name="fileSystem">The file system used to inspect file content.</param>
    public BinaryFileFilter(IFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public bool Include(FileCandidate candidate, CollectionOptions options)
    {
        if (!options.IgnoreBinaryFiles)
            return true;

        try
        {
            return !_fileSystem.IsBinaryFile(candidate.FullPath);
        }
        catch
        {
            // Unreadable files cannot be classified; exclude them rather than fail the pipeline.
            return false;
        }
    }
}
