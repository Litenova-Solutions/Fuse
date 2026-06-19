using Fuse.Collection.Models;
using Fuse.Emission.Models;
using Fuse.Emission.Writers;
using Fuse.Reduction.Models;

namespace Fuse.Emission.Tests;

public sealed class EmissionPipelineOrderingTests
{
    [Fact]
    public async Task EmitAsync_OrdersEntriesByTokenCountDescending()
    {
        var entries = new[]
        {
            CreateEntry("small.cs", "x", tokenCount: 5),
            CreateEntry("large.cs", new string('a', 200), tokenCount: 200),
            CreateEntry("medium.cs", new string('b', 50), tokenCount: 50),
        };

        var pipeline = new EmissionPipeline();
        var writer = new RecordingOutputWriter();
        var options = new EmissionOptions
        {
            IncludeManifest = false,
            MaxTokens = 10000,
        };

        await pipeline.EmitAsync(entries, options, writer);

        Assert.Equal(["large.cs", "medium.cs", "small.cs"], writer.EntryPaths);
    }

  [Fact]
  public async Task EmitAsync_SplitBudget_RotatesToSecondPart()
  {
    var entries = new[]
    {
      CreateEntry("first.cs", new string('a', 80), tokenCount: 80),
      CreateEntry("second.cs", new string('b', 80), tokenCount: 80),
    };

    var pipeline = new EmissionPipeline();
    var writer = new RecordingOutputWriter();
    var options = new EmissionOptions
    {
      IncludeManifest = false,
      MaxTokens = 120,
      SplitTokens = 120,
    };

    await pipeline.EmitAsync(entries, options, writer);

    Assert.Equal(2, writer.PartCount);
    Assert.Single(writer.PathsPerPart[0]);
    Assert.Single(writer.PathsPerPart[1]);
  }

    private static FusedContent CreateEntry(string path, string body, int tokenCount)
    {
        var candidate = new FileCandidate(path, path, new FileInfo(path));
        var source = new SourceFile(candidate);
        return new FusedContent(source, body, new StaticTokenCounter(tokenCount));
    }

    private sealed class StaticTokenCounter(int count) : Fuse.Reduction.Tokenization.ITokenCounter
    {
        public int Count(string content) => count;
    }

    private sealed class RecordingOutputWriter : IOutputWriter
    {
        public List<string> EntryPaths { get; } = [];
        public List<List<string>> PathsPerPart { get; } = [[]];
        public int PartCount => PathsPerPart.Count;

        public bool SupportsMultiPart => true;

        public Task WritePrefixAsync(string prefix, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task WriteEntryAsync(FusedContent content, CancellationToken cancellationToken = default)
        {
            EntryPaths.Add(content.NormalizedPath);
            PathsPerPart[^1].Add(content.NormalizedPath);
            return Task.CompletedTask;
        }

        public Task RotatePartAsync(CancellationToken cancellationToken = default)
        {
            PathsPerPart.Add([]);
            return Task.CompletedTask;
        }

        public Task<FusionResult> CompleteAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FusionResult([], null, 0, 0, 0, TimeSpan.Zero, []));
    }
}
