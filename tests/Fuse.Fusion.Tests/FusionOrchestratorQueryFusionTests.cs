using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// Item 2: multi-query fusion combines the raw, identifier-only, and PRF-expanded rankings with RRF. Gated by
// the MultiQueryFusion knob; this verifies the fused path still selects the relevant files and is recall-safe.
public sealed class FusionOrchestratorQueryFusionTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorQueryFusionTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-query-fusion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        WriteFile("PaymentService.cs", """
            public class PaymentService
            {
                public void ProcessPayment() { }
            }
            """);
        WriteFile("CatalogService.cs", """
            public class CatalogService
            {
                public void ListProducts() { }
            }
            """);

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_FusionOn_SurfacesTheNamedType()
    {
        var result = await FuseAsync(new ExperimentalOptions { MultiQueryFusion = true });

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("PaymentService.cs", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_FusionOnAndOff_BothSurfaceTheNamedType()
    {
        // Fusion is a recall-safe reranking: it must not drop the file the plain query already finds.
        var on = await FuseAsync(new ExperimentalOptions { MultiQueryFusion = true });
        var off = await FuseAsync(new ExperimentalOptions { MultiQueryFusion = false });

        Assert.Contains("PaymentService.cs", on.InMemoryContent!);
        Assert.Contains("PaymentService.cs", off.InMemoryContent!);
    }

    private async Task<FusionResult> FuseAsync(ExperimentalOptions experimental)
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = false },
            inMemory: true,
            query: new QueryOptions("process PaymentService payment", TopFiles: 2, Depth: 1),
            experimental: experimental);

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
