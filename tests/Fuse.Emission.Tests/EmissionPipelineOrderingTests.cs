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
        // SplitTokens (per-part size) and MaxTokens (hard total cap) are independent axes: the part size forces
        // a rotation between the two entries, while the total cap is generous enough to admit both. Each entry
        // costs 80 plus the per-entry marker overhead, so a 120-token part holds one at a time.
        var options = new EmissionOptions
        {
            IncludeManifest = false,
            MaxTokens = 10000,
            SplitTokens = 120,
        };

        await pipeline.EmitAsync(entries, options, writer);

        Assert.Equal(2, writer.PartCount);
        Assert.Single(writer.PathsPerPart[0]);
        Assert.Single(writer.PathsPerPart[1]);
    }

    [Fact]
    public async Task EmitAsync_WhenScored_EmitsMostRelevantFirstUnderBudget()
    {
        // A small, highly relevant seed and two large, less relevant files. Under a tight budget the seed
        // must be emitted first (so it survives), and the least relevant file must be dropped.
        var seed = CreateEntry("seed.cs", "x", tokenCount: 20).WithRelevanceScore(1.0);
        var bulky = CreateEntry("bulky.cs", new string('a', 500), tokenCount: 500).WithRelevanceScore(0.25);
        var huge = CreateEntry("huge.cs", new string('b', 800), tokenCount: 800).WithRelevanceScore(0.10);

        var pipeline = new EmissionPipeline();
        var writer = new RecordingOutputWriter();
        var options = new EmissionOptions
        {
            IncludeManifest = false,
            MaxTokens = 100,
        };

        // Supplied in the opposite order to prove ordering is by relevance, not input order.
        await pipeline.EmitAsync([huge, bulky, seed], options, writer);

        Assert.Equal("seed.cs", writer.EntryPaths[0]);
        Assert.DoesNotContain("huge.cs", writer.EntryPaths);
    }

    [Fact]
    public async Task EmitAsync_Unscored_RetainsDescendingTokenOrder()
    {
        var entries = new[]
        {
            CreateEntry("small.cs", "x", tokenCount: 5),
            CreateEntry("large.cs", new string('a', 200), tokenCount: 200),
        };

        var pipeline = new EmissionPipeline();
        var writer = new RecordingOutputWriter();
        var options = new EmissionOptions { IncludeManifest = false, MaxTokens = 10000 };

        await pipeline.EmitAsync(entries, options, writer);

        Assert.Equal(["large.cs", "small.cs"], writer.EntryPaths);
    }

    [Fact]
    public async Task EmitAsync_DoesNotWriteEntryThatWouldExceedMaxTokens()
    {
        // Two equal entries: the first fits, the second would cross the hard cap. The over-budget entry must
        // not be written, and the reported total must stay within MaxTokens.
        var first = CreateEntry("first.cs", "a", tokenCount: 50).WithRelevanceScore(1.0);
        var second = CreateEntry("second.cs", "b", tokenCount: 50).WithRelevanceScore(0.5);

        var pipeline = new EmissionPipeline();
        var writer = new RecordingOutputWriter();
        // Each entry costs 50 plus the 30-token marker overhead (80). The budget admits one but not two.
        var options = new EmissionOptions { IncludeManifest = false, MaxTokens = 120 };

        var result = await pipeline.EmitAsync([first, second], options, writer);

        Assert.Equal(["first.cs"], writer.EntryPaths);
        Assert.True(result.TotalTokens <= options.MaxTokens);
    }

    [Fact]
    public async Task EmitAsync_AlwaysWritesMostRelevantEntryEvenWhenAloneOverBudget()
    {
        // The single closest match must survive even if it alone exceeds the budget, so a scoped run never
        // emits nothing.
        var seed = CreateEntry("seed.cs", new string('a', 500), tokenCount: 500).WithRelevanceScore(1.0);

        var pipeline = new EmissionPipeline();
        var writer = new RecordingOutputWriter();
        var options = new EmissionOptions { IncludeManifest = false, MaxTokens = 100 };

        await pipeline.EmitAsync([seed], options, writer);

        Assert.Equal(["seed.cs"], writer.EntryPaths);
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
