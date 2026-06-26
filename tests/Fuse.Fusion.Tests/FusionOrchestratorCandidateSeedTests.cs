using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// A4: candidate pool vs seed count. SeedTopK limits the files promoted to expansion seeds; CandidateTopK widens
// the BM25 pool a reranker would reorder without changing the seed count.
public sealed class FusionOrchestratorCandidateSeedTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorCandidateSeedTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-candidate-seed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        // Independent files that all match the query, with no dependency edges, so the seed count equals the
        // included-file count.
        for (var i = 0; i < 6; i++)
        {
            WriteFile($"PaymentHandler{i}.cs", $$"""
                public class PaymentHandler{{i}}
                {
                    public void ProcessPayment() { var step = "process-payment-{{i}}"; }
                }
                """);
        }

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_SeedTopK_LimitsTheSeedSet()
    {
        var oneSeed = await CountEmittedFilesAsync(new QueryOptions("process payment", TopFiles: 6, Depth: 1, SeedTopK: 1));
        var fiveSeeds = await CountEmittedFilesAsync(new QueryOptions("process payment", TopFiles: 6, Depth: 1, SeedTopK: 5));

        Assert.Equal(1, oneSeed);
        Assert.True(fiveSeeds > oneSeed);
    }

    [Fact]
    public async Task FuseAsync_WiderCandidatePool_DoesNotChangeSeedCount()
    {
        // A wider candidate pool (what a reranker reorders) with the same seed count must not change how many
        // files are seeded on the lexical path.
        var narrow = await CountEmittedFilesAsync(new QueryOptions("process payment", TopFiles: 6, Depth: 1, SeedTopK: 2));
        var widePool = await CountEmittedFilesAsync(new QueryOptions("process payment", TopFiles: 6, Depth: 1, SeedTopK: 2, CandidateTopK: 50));

        Assert.Equal(narrow, widePool);
    }

    private async Task<int> CountEmittedFilesAsync(QueryOptions query)
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = false },
            inMemory: true,
            query: query);

        var result = await orchestrator.FuseAsync(request);
        Assert.NotNull(result.InMemoryContent);
        return System.Text.RegularExpressions.Regex.Matches(result.InMemoryContent!, "PaymentHandler\\d+\\.cs").Count;
    }

    private void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_sourceDirectory, name), content);

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }
}
