using Fuse.Fusion.Scoping;
using Fuse.Plugins.Rerank.Onnx;

namespace Fuse.Plugins.Rerank.Onnx.Tests;

public sealed class OnnxRerankTests
{
    [Fact]
    public void ModelDirectory_HonorsFuseUserData()
    {
        var temp = Path.Combine(Path.GetTempPath(), "fuse-model-loc", Guid.NewGuid().ToString("N"));
        var original = Environment.GetEnvironmentVariable("FUSE_USER_DATA");
        try
        {
            Environment.SetEnvironmentVariable("FUSE_USER_DATA", temp);
            var dir = RerankModelLocator.ModelDirectory();

            Assert.StartsWith(temp, dir);
            Assert.Contains(RerankModelLocator.ModelId, dir);
            Assert.False(RerankModelLocator.IsModelPresent());
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUSE_USER_DATA", original);
        }
    }

    [Fact]
    public void Reranker_AbsentModel_IsUnavailableAndKeepsOrder()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), "fuse-no-model", Guid.NewGuid().ToString("N"));
        var reranker = new OnnxDenseReranker(
            Path.Combine(missingDir, "model.onnx"),
            Path.Combine(missingDir, "vocab.txt"));

        Assert.False(reranker.IsAvailable);

        var candidates = new List<RankedFile>
        {
            new("a.cs", 3.0),
            new("b.cs", 2.0),
            new("c.cs", 1.0),
        };
        var result = reranker.Rerank("query", candidates, new Dictionary<string, string>());

        // Absent a model the reranker is a no-op: the lexical order is preserved exactly.
        Assert.Equal(candidates.Select(c => c.Path), result.Select(r => r.Path));
    }

    [Fact]
    public void Reranker_SingleCandidate_ReturnsInput()
    {
        var reranker = new OnnxDenseReranker("missing.onnx", "missing.txt");
        var candidates = new List<RankedFile> { new("only.cs", 1.0) };

        var result = reranker.Rerank("query", candidates, new Dictionary<string, string>());

        Assert.Same(candidates, result);
    }

    [Fact]
    public void Verify_MissingFile_IsFalse()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin");
        Assert.False(RerankModelDownloader.Verify(missing, RerankModelDownloader.Files[0].Sha256));
    }

    [Fact]
    public void Verify_HashMismatch_IsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(path, "not the model");
        try
        {
            Assert.False(RerankModelDownloader.Verify(path, RerankModelDownloader.Files[0].Sha256));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Files_ArePinnedWithHashes()
    {
        Assert.NotEmpty(RerankModelDownloader.Files);
        Assert.All(RerankModelDownloader.Files, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Sha256));
            Assert.Equal(64, f.Sha256.Length); // SHA-256 hex
            Assert.StartsWith("https://", f.Url);
        });
    }
}
