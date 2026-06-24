using System.Security.Cryptography;

namespace Fuse.Plugins.Rerank.Onnx;

/// <summary>
///     Downloads and verifies the dense rerank model files into the user-data cache, and reports or clears
///     them. This is the explicit, opt-in fetch the CLI <c>fuse models</c> command drives; nothing here runs
///     during scoping.
/// </summary>
/// <remarks>
///     Files come from the public ONNX export of all-MiniLM-L6-v2 and are pinned by SHA-256, so a truncated or
///     tampered download is rejected rather than loaded. The total is about 23 MB (a quantized graph plus a
///     vocabulary).
/// </remarks>
public static class RerankModelDownloader
{
    /// <summary>One file the model needs: where it goes in the cache, where it comes from, and its expected hash.</summary>
    /// <param name="RelativePath">Path under the model directory, using <c>/</c> separators.</param>
    /// <param name="Url">Source URL.</param>
    /// <param name="Sha256">Expected lowercase hex SHA-256 of the file contents.</param>
    /// <param name="Bytes">Approximate size in bytes, for the size shown before download.</param>
    public sealed record ModelFile(string RelativePath, string Url, string Sha256, long Bytes);

    private const string BaseUrl = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main";

    /// <summary>The files that make up the cached model, in download order.</summary>
    public static IReadOnlyList<ModelFile> Files { get; } =
    [
        new("onnx/model_quantized.onnx", $"{BaseUrl}/onnx/model_quantized.onnx",
            "afdb6f1a0e45b715d0bb9b11772f032c399babd23bfc31fed1c170afc848bdb1", 22_972_370),
        new("vocab.txt", $"{BaseUrl}/vocab.txt",
            "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3", 231_508),
    ];

    /// <summary>The total download size in bytes across all files.</summary>
    public static long TotalBytes => Files.Sum(f => f.Bytes);

    /// <summary>
    ///     Downloads any missing or corrupt model file into the cache, verifying each against its pinned hash.
    /// </summary>
    /// <param name="progress">Optional sink for human-readable per-file progress lines.</param>
    /// <param name="cancellationToken">Token used to cancel the download.</param>
    /// <returns>A task that completes when every file is present and verified.</returns>
    /// <exception cref="InvalidOperationException">Thrown when a downloaded file fails its integrity check.</exception>
    public static async Task DownloadAsync(IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var directory = RerankModelLocator.ModelDirectory();

        foreach (var file in Files)
        {
            var destination = Path.Combine(directory, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(destination) && Verify(destination, file.Sha256))
            {
                progress?.Report($"{file.RelativePath}: already present");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            progress?.Report($"downloading {file.RelativePath} (~{file.Bytes / 1_000_000} MB)...");

            var bytes = await http.GetByteArrayAsync(file.Url, cancellationToken);
            await File.WriteAllBytesAsync(destination, bytes, cancellationToken);

            if (!Verify(destination, file.Sha256))
            {
                File.Delete(destination);
                throw new InvalidOperationException(
                    $"Integrity check failed for {file.RelativePath}; the download was removed.");
            }

            progress?.Report($"{file.RelativePath}: downloaded and verified");
        }
    }

    /// <summary>
    ///     Verifies a file against an expected SHA-256.
    /// </summary>
    /// <param name="path">The file to hash.</param>
    /// <param name="expectedSha256">The expected lowercase hex digest.</param>
    /// <returns><see langword="true" /> when the file's hash matches; otherwise <see langword="false" />.</returns>
    public static bool Verify(string path, string expectedSha256)
    {
        if (!File.Exists(path))
            return false;

        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Removes the cached model directory, if present.
    /// </summary>
    /// <returns><see langword="true" /> when a directory was deleted; <see langword="false" /> when none existed.</returns>
    public static bool Remove()
    {
        var directory = RerankModelLocator.ModelDirectory();
        if (!Directory.Exists(directory))
            return false;

        Directory.Delete(directory, recursive: true);
        return true;
    }
}
