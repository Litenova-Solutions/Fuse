namespace Fuse.Fusion.Embeddings.Onnx;

/// <summary>
///     Fetches a model file to a local path. An injectable seam so tests can supply a fixture or assert that no
///     network call is attempted (for example when a model is sideloaded).
/// </summary>
public interface IEmbeddingModelDownloader
{
    /// <summary>
    ///     Downloads <paramref name="file" /> to <paramref name="destinationPath" />.
    /// </summary>
    /// <param name="file">The pinned file descriptor (URL and expected hash).</param>
    /// <param name="destinationPath">The absolute path to write the file to.</param>
    /// <param name="cancellationToken">Token used to cancel the download.</param>
    /// <returns><see langword="true" /> when the file was fetched, <see langword="false" /> when it could not be.</returns>
    Task<bool> TryDownloadAsync(EmbeddingModelFile file, string destinationPath, CancellationToken cancellationToken = default);
}
