using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// Reduction-aware single-pass packing (roadmap 3.1): the token budget is applied to the real reduced token
// cost, so emitted output fills the budget tightly instead of being starved by a pre-reduction byte estimate.
public sealed class FusionOrchestratorPackingTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorPackingTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-packing-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        // Many small, independent files that all match the query, so packing has a large candidate pool to
        // fill a budget with and no dependency edges to widen the set.
        for (var i = 0; i < 40; i++)
        {
            WriteFile($"PaymentHandler{i}.cs", $$"""
                public class PaymentHandler{{i}}
                {
                    public void ProcessPayment()
                    {
                        var step = "process-payment-body-{{i}}";
                    }
                }
                """);
        }

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(700)]
    [InlineData(1000)]
    public async Task FuseAsync_Query_EmittedTokensFillBudget(int maxTokens)
    {
        var result = await FuseQueryAsync(maxTokens);

        // The emitted token total lands tightly under the budget: within 85 to 100 percent of MaxTokens.
        Assert.True(result.TotalTokens <= maxTokens,
            $"emitted {result.TotalTokens} exceeded budget {maxTokens}");
        Assert.True(result.TotalTokens >= 0.85 * maxTokens,
            $"emitted {result.TotalTokens} under-filled budget {maxTokens}");
    }

    [Fact]
    public async Task FuseAsync_Query_PackingIsDeterministic()
    {
        var first = await FuseQueryAsync(700);
        var second = await FuseQueryAsync(700);

        Assert.Equal(first.InMemoryContent, second.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_Query_ReductionCacheServesDroppedCandidates()
    {
        // With a tight budget many candidates are reduced but dropped from the emitted set. The reduction
        // cache must still serve them on a warm run, so the second pass is a near-complete cache hit.
        await FuseQueryAsync(300, useReductionCache: true);
        var warm = await FuseQueryAsync(300, useReductionCache: true);

        Assert.True(warm.ReductionCacheHits > 0,
            "expected warm run to serve reduced candidates (including dropped ones) from cache");
    }

    private async Task<FusionResult> FuseQueryAsync(int maxTokens, bool useReductionCache = false)
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { MaxTokens = maxTokens },
            inMemory: true,
            query: new QueryOptions("process payment", TopFiles: 40, Depth: 1),
            useReductionCache: useReductionCache);

        return await orchestrator.FuseAsync(request);
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
