using System.Security.Cryptography;

namespace Fuse.Fusion.Embeddings.Onnx;

/// <summary>
///     Resolves a model's files to local paths: honoring a sideload directory, reusing a per-machine cache, or
///     downloading and verifying against the pinned SHA-256. Returns <see langword="null" /> when the files are
///     unavailable (offline with nothing cached, or a hash mismatch), so the caller can fall back to hashing.
/// </summary>
public sealed class EmbeddingModelResolver
{
    /// <summary>
    ///     The environment variable that points to a directory holding the model files. When set, the resolver
    ///     loads from there and never touches the network.
    /// </summary>
    public const string SideloadPathVariable = "FUSE_EMBEDDINGS_MODEL_PATH";

    private readonly IEmbeddingModelDownloader _downloader;
    private readonly string _cacheRoot;

    /// <summary>
    ///     Initializes a new instance of the <see cref="EmbeddingModelResolver" /> class.
    /// </summary>
    /// <param name="downloader">The seam used to fetch missing files.</param>
    /// <param name="cacheRoot">
    ///     The root of the per-machine model cache. Defaults to <c>~/.fuse/models</c> when <c>null</c>.
    /// </param>
    public EmbeddingModelResolver(IEmbeddingModelDownloader downloader, string? cacheRoot = null)
    {
        _downloader = downloader;
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fuse", "models");
    }

    /// <summary>
    ///     Resolves the model and vocabulary file paths for <paramref name="descriptor" />.
    /// </summary>
    /// <param name="descriptor">The pinned model descriptor.</param>
    /// <param name="cancellationToken">Token used to cancel any download.</param>
    /// <returns>The local file paths, or <see langword="null" /> when the model cannot be made available.</returns>
    public async Task<ResolvedEmbeddingModel?> ResolveAsync(
        EmbeddingModelDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        var sideload = Environment.GetEnvironmentVariable(SideloadPathVariable);
        if (!string.IsNullOrWhiteSpace(sideload))
        {
            // Sideload is an explicit override: load from the directory and never download, even on a miss.
            var modelPath = Path.Combine(sideload, descriptor.ModelFile.FileName);
            var vocabPath = Path.Combine(sideload, descriptor.VocabFile.FileName);
            return File.Exists(modelPath) && File.Exists(vocabPath)
                ? new ResolvedEmbeddingModel(descriptor, modelPath, vocabPath)
                : null;
        }

        var cacheDir = Path.Combine(_cacheRoot, descriptor.Name);
        var model = await EnsureFileAsync(cacheDir, descriptor.ModelFile, cancellationToken);
        if (model is null)
            return null;

        var vocab = await EnsureFileAsync(cacheDir, descriptor.VocabFile, cancellationToken);
        if (vocab is null)
            return null;

        return new ResolvedEmbeddingModel(descriptor, model, vocab);
    }

    // Returns the local path of a verified file, downloading it if absent. A hash mismatch (corrupt or tampered
    // download) is refused: the file is deleted and null returned.
    private async Task<string?> EnsureFileAsync(string cacheDir, EmbeddingModelFile file, CancellationToken cancellationToken)
    {
        var path = Path.Combine(cacheDir, file.FileName);
        if (File.Exists(path))
        {
            if (HashMatches(path, file.Sha256))
                return path;

            // A stale or partial cached file: drop it and re-fetch.
            File.Delete(path);
        }

        if (!await _downloader.TryDownloadAsync(file, path, cancellationToken))
            return null;

        if (HashMatches(path, file.Sha256))
            return path;

        Console.Error.WriteLine($"fuse: embedding model file {file.FileName} failed SHA-256 verification; refusing it");
        if (File.Exists(path))
            File.Delete(path);
        return null;
    }

    private static bool HashMatches(string path, string expectedSha256)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     The resolved local file paths for a model.
/// </summary>
/// <param name="Descriptor">The model descriptor the paths belong to.</param>
/// <param name="ModelPath">The local path of the ONNX weights file.</param>
/// <param name="VocabPath">The local path of the vocabulary file.</param>
public sealed record ResolvedEmbeddingModel(EmbeddingModelDescriptor Descriptor, string ModelPath, string VocabPath);
