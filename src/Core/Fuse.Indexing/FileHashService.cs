using System.IO.Hashing;

namespace Fuse.Indexing;

/// <summary>
///     Computes content hashes for change detection in the workspace index.
/// </summary>
/// <remarks>
///     Uses XxHash64: fast and collision-resistant enough to decide whether a file changed and must be
///     re-indexed. The hash is not used for security, only for cache invalidation.
/// </remarks>
public sealed class FileHashService
{
    /// <summary>
    ///     Computes the content hash of a byte span.
    /// </summary>
    /// <param name="content">The bytes to hash.</param>
    /// <returns>A 16-character lowercase hexadecimal hash.</returns>
    public string ComputeHash(ReadOnlySpan<byte> content) =>
        XxHash64.HashToUInt64(content).ToString("x16");

    /// <summary>
    ///     Reads a file and computes its content hash.
    /// </summary>
    /// <param name="path">The absolute path to the file.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>A 16-character lowercase hexadecimal hash of the file content.</returns>
    public async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return ComputeHash(bytes);
    }
}
