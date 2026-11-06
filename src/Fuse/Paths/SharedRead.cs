namespace Fuse.Paths;

/// <summary>
///     Reads a file in the working tree while letting every other process write, rename or delete it. Agents save files
///     while the engine reads them; a read that allowed only other readers would make an agent's save fail on Windows
///     for as long as it held the file open.
/// </summary>
internal static class SharedRead
{
    /// <summary>The bytes of <paramref name="path"/>, opened so that a writer is never refused.</summary>
    /// <exception cref="IOException">The file cannot be read, as <see cref="File.ReadAllBytes(string)"/> throws.</exception>
    public static byte[] AllBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.CanSeek && stream.Length <= int.MaxValue)
        {
            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            return buffer;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>The bytes of <paramref name="path"/>, read asynchronously and opened so that a writer is never refused.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static async Task<byte[]> AllBytesAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
            return copy.ToArray();
        }
    }

    /// <summary>The text of <paramref name="path"/> as UTF-8 with a byte-order mark honored, opened so that a writer is never refused.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static async Task<string> AllTextAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
