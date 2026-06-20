using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

public sealed class FusionOrchestratorRerankTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorRerankTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-rerank-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);
        File.WriteAllText(Path.Combine(_sourceDirectory, "OrderService.cs"),
            "public class OrderService { public void PlaceOrder() { } public void Charge() { } }");
        File.WriteAllText(Path.Combine(_sourceDirectory, "XmlSerializer.cs"),
            "public class XmlConfigSerializer { public string Serialize() { return string.Empty; } }");

        var services = new ServiceCollection();
        services.AddFuse();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_QueryWithRerank_ReturnsRelevantFile()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = false },
            inMemory: true,
            query: new QueryOptions("order charge payment", TopFiles: 10, Depth: 1, Rerank: true));

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("OrderService", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_RerankAndNoRerank_BothProduceOutput()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        FusionRequest Make(bool rerank) => new(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = false },
            inMemory: true,
            query: new QueryOptions("order charge", TopFiles: 10, Depth: 1, Rerank: rerank));

        var plain = await orchestrator.FuseAsync(Make(false));
        var reranked = await orchestrator.FuseAsync(Make(true));

        Assert.False(string.IsNullOrEmpty(plain.InMemoryContent));
        Assert.False(string.IsNullOrEmpty(reranked.InMemoryContent));
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }
}
