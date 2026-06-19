namespace Fuse.Collection.FileSystem;

/// <summary>
///     Provides a concrete implementation of <see cref="IFileSystem" /> that operates
///     on the physical file system using <see cref="System.IO" /> classes.
/// </summary>
/// <remarks>
///     This class maintains no state and is safe to register as a singleton in the DI container.
/// </remarks>
public sealed class PhysicalFileSystem : IFileSystem
{
    /// <inheritdoc />
    public bool DirectoryExists(string path)
    {
        return Directory.Exists(path);
    }

    /// <inheritdoc />
    public void CreateDirectory(string path)
    {
        Directory.CreateDirectory(path);
    }

    /// <inheritdoc />
    public IEnumerable<string> EnumerateFiles(string path, string searchPattern, SearchOption searchOption)
    {
        return Directory.EnumerateFiles(path, searchPattern, searchOption);
    }

    /// <inheritdoc />
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default)
    {
        return File.ReadAllTextAsync(path, cancellationToken);
    }

    /// <inheritdoc />
    public FileInfo GetFileInfo(string path)
    {
        return new FileInfo(path);
    }

    /// <inheritdoc />
    public string GetRelativePath(string relativeTo, string path)
    {
        return Path.GetRelativePath(relativeTo, path);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Samples the first 8000 bytes as raw bytes. If any byte equals <c>0x00</c>,
    ///     the file is classified as binary.
    /// </remarks>
    public bool IsBinaryFile(string filePath)
    {
        const int bytesToCheck = 8000;

        using var stream = File.OpenRead(filePath);
        var buffer = new byte[bytesToCheck];
        var bytesRead = stream.Read(buffer, 0, bytesToCheck);

        if (bytesRead == 0)
            return false;

        for (var i = 0; i < bytesRead; i++)
        {
            if (buffer[i] == 0x00)
                return true;
        }

        return false;
    }
}
