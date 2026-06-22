namespace Fuse.Fusion.Embeddings.Onnx;

/// <summary>
///     Default <see cref="IEmbeddingModelDownloader" /> that fetches files over HTTP, printing a one-line
///     stderr notice on first download. A failure (offline, transport error) returns <see langword="false" />
///     so the caller can fall back to the hashing embedding rather than throwing.
/// </summary>
public sealed class HttpEmbeddingModelDownloader : IEmbeddingModelDownloader
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <inheritdoc />
    public async Task<bool> TryDownloadAsync(
        EmbeddingModelFile file,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var approxMb = Math.Max(1, file.SizeBytes / (1024 * 1024));
            Console.Error.WriteLine($"fuse: downloading embedding model file {file.FileName} (~{approxMb} MB), one time");

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            // Write to a temp file first so a cancelled or failed download never leaves a partial file that a
            // later run would treat as present.
            var temp = destinationPath + ".download";
            await using (var response = await Client.GetStreamAsync(file.Url, cancellationToken))
            await using (var target = File.Create(temp))
            {
                await response.CopyToAsync(target, cancellationToken);
            }

            File.Move(temp, destinationPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Console.Error.WriteLine($"fuse: embedding model download failed ({ex.Message}); falling back to hashing embeddings");
            return false;
        }
    }
}
