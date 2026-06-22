using System.Security.Cryptography;
using Fuse.Fusion.Embeddings.Onnx;

namespace Fuse.Fusion.Embeddings.Onnx.Tests;

public sealed class EmbeddingModelResolverTests : IDisposable
{
    private readonly string _root;

    public EmbeddingModelResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-onnx-resolver", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable(EmbeddingModelResolver.SideloadPathVariable, null);
    }

    [Fact]
    public async Task Sideload_LoadsFromPath_AndDoesNotDownload()
    {
        var dir = Path.Combine(_root, "sideload");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "model.onnx"), "weights");
        File.WriteAllText(Path.Combine(dir, "vocab.txt"), "vocab");
        Environment.SetEnvironmentVariable(EmbeddingModelResolver.SideloadPathVariable, dir);

        var spy = new SpyDownloader(_ => null);
        var resolver = new EmbeddingModelResolver(spy, _root);

        var resolved = await resolver.ResolveAsync(EmbeddingModelDescriptor.Default);

        Assert.NotNull(resolved);
        Assert.Equal(0, spy.Calls); // sideload never touches the network
    }

    [Fact]
    public async Task Sideload_MissingFiles_ReturnsNull()
    {
        var dir = Path.Combine(_root, "empty");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(EmbeddingModelResolver.SideloadPathVariable, dir);

        var spy = new SpyDownloader(_ => null);
        var resolved = await new EmbeddingModelResolver(spy, _root).ResolveAsync(EmbeddingModelDescriptor.Default);

        Assert.Null(resolved);
        Assert.Equal(0, spy.Calls);
    }

    [Fact]
    public async Task Download_CorrectHash_ResolvesAndCaches()
    {
        var modelBytes = "the-onnx-weights"u8.ToArray();
        var vocabBytes = "the-vocabulary"u8.ToArray();
        var descriptor = DescriptorFor(modelBytes, vocabBytes);

        var spy = new SpyDownloader(f => f.FileName == "model.onnx" ? modelBytes : vocabBytes);
        var resolver = new EmbeddingModelResolver(spy, _root);

        var resolved = await resolver.ResolveAsync(descriptor);

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved!.ModelPath));
        Assert.True(File.Exists(resolved.VocabPath));
        Assert.Equal(2, spy.Calls);
    }

    [Fact]
    public async Task Download_HashMismatch_IsRejectedAndDeleted()
    {
        var modelBytes = "the-onnx-weights"u8.ToArray();
        var vocabBytes = "the-vocabulary"u8.ToArray();
        var descriptor = DescriptorFor(modelBytes, vocabBytes);

        // The downloader writes corrupt bytes that do not match the pinned hash.
        var spy = new SpyDownloader(_ => "corrupt-payload"u8.ToArray());
        var resolver = new EmbeddingModelResolver(spy, _root);

        var resolved = await resolver.ResolveAsync(descriptor);

        Assert.Null(resolved);
        Assert.False(File.Exists(Path.Combine(_root, descriptor.Name, "model.onnx")));
    }

    [Fact]
    public async Task Download_Fails_ReturnsNull()
    {
        var descriptor = DescriptorFor("m"u8.ToArray(), "v"u8.ToArray());
        var spy = new SpyDownloader(_ => null); // simulates offline

        var resolved = await new EmbeddingModelResolver(spy, _root).ResolveAsync(descriptor);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task CachedFiles_AreReusedWithoutDownloading()
    {
        var modelBytes = "the-onnx-weights"u8.ToArray();
        var vocabBytes = "the-vocabulary"u8.ToArray();
        var descriptor = DescriptorFor(modelBytes, vocabBytes);

        var cacheDir = Path.Combine(_root, descriptor.Name);
        Directory.CreateDirectory(cacheDir);
        File.WriteAllBytes(Path.Combine(cacheDir, "model.onnx"), modelBytes);
        File.WriteAllBytes(Path.Combine(cacheDir, "vocab.txt"), vocabBytes);

        var spy = new SpyDownloader(_ => null);
        var resolved = await new EmbeddingModelResolver(spy, _root).ResolveAsync(descriptor);

        Assert.NotNull(resolved);
        Assert.Equal(0, spy.Calls); // verified cache hit, no download
    }

    private static EmbeddingModelDescriptor DescriptorFor(byte[] modelBytes, byte[] vocabBytes)
    {
        string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
        return new EmbeddingModelDescriptor(
            "test-model",
            Dimensions: 4,
            MaxTokens: 8,
            new EmbeddingModelFile("model.onnx", "https://example/model", Sha(modelBytes), modelBytes.Length),
            new EmbeddingModelFile("vocab.txt", "https://example/vocab", Sha(vocabBytes), vocabBytes.Length));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EmbeddingModelResolver.SideloadPathVariable, null);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class SpyDownloader(Func<EmbeddingModelFile, byte[]?> bytesFor) : IEmbeddingModelDownloader
    {
        public int Calls { get; private set; }

        public Task<bool> TryDownloadAsync(EmbeddingModelFile file, string destinationPath, CancellationToken cancellationToken = default)
        {
            Calls++;
            var bytes = bytesFor(file);
            if (bytes is null)
                return Task.FromResult(false);

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, bytes);
            return Task.FromResult(true);
        }
    }
}
