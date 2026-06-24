using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// Item 1: tiered emission skeletonizes dependency-expanded neighbours (provenance hop two or deeper) so the
// packer fits more files under a budget, while seeds keep their bodies. Gated by the TieredEmission knob.
public sealed class FusionOrchestratorTieredEmissionTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorTieredEmissionTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-tiered", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        // PaymentService is the query seed; it depends on PaymentGateway, which is pulled in as a neighbour.
        WriteFile("PaymentService.cs", """
            public class PaymentService
            {
                private readonly PaymentGateway _gateway = new();
                public void ProcessPayment()
                {
                    var token = "seed-body-marker-token";
                }
            }
            """);
        WriteFile("PaymentGateway.cs", """
            public class PaymentGateway
            {
                public void Charge()
                {
                    var pin = "neighbour-body-marker-token";
                }
            }
            """);

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_TieredOn_SkeletonizesNeighbourKeepsSeedBody()
    {
        var result = await FuseQueryAsync(tiered: true);

        Assert.NotNull(result.InMemoryContent);
        // The neighbour is pulled in for context...
        Assert.Contains("PaymentGateway", result.InMemoryContent);
        // ...but as a signature skeleton: its method name survives, its body does not.
        Assert.Contains("Charge", result.InMemoryContent);
        Assert.DoesNotContain("neighbour-body-marker-token", result.InMemoryContent);
        // The seed keeps its body.
        Assert.Contains("seed-body-marker-token", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_TieredOff_KeepsNeighbourBody()
    {
        var result = await FuseQueryAsync(tiered: false);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("PaymentGateway", result.InMemoryContent);
        // With tiering off the neighbour keeps its full body.
        Assert.Contains("neighbour-body-marker-token", result.InMemoryContent);
    }

    private async Task<FusionResult> FuseQueryAsync(bool tiered)
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(level: ReductionLevel.None),
            new EmissionOptions(),
            inMemory: true,
            query: new QueryOptions("process payment", TopFiles: 1, Depth: 1),
            experimental: new ExperimentalOptions { TieredEmission = tiered });

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
