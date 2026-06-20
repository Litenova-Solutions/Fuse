using Fuse.Emission.Models;
using Fuse.Fusion.Extensions;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

public sealed class FusionOrchestratorTableOfContentsTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorTableOfContentsTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-toc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_sourceDirectory, "Services"));
        File.WriteAllText(
            Path.Combine(_sourceDirectory, "Services", "OrderService.cs"),
            """
            public class OrderService
            {
                public void PlaceOrder()
                {
                    var x = 1;
                }
            }
            """);
        File.WriteAllText(
            Path.Combine(_sourceDirectory, "Order.cs"),
            """
            public class Order
            {
                public int Id { get; set; }
            }
            """);

        var services = new ServiceCollection();
        services.AddFuse();
        _serviceProvider = services.BuildServiceProvider();
    }

    private FusionRequest Request() => new(
        new Fuse.Collection.Options.CollectionOptions(_sourceDirectory, extensions: [".cs"]),
        new ReductionOptions(),
        new EmissionOptions { TableOfContents = true, IncludeManifest = false },
        inMemory: true);

    [Fact]
    public async Task FuseAsync_TableOfContents_EmitsTreeAndOutlineNotBodies()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        var result = await orchestrator.FuseAsync(Request());

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("fuse:table-of-contents", result.InMemoryContent);
        Assert.Contains("OrderService.cs", result.InMemoryContent);
        Assert.Contains("class OrderService: PlaceOrder", result.InMemoryContent);
        // The cheap survey must not emit method bodies.
        Assert.DoesNotContain("var x = 1", result.InMemoryContent);
        Assert.DoesNotContain("<file path", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_TableOfContents_CostsFewerTokensThanFullFetch()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        var toc = await orchestrator.FuseAsync(Request());
        var full = await orchestrator.FuseAsync(new FusionRequest(
            new Fuse.Collection.Options.CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = false },
            inMemory: true));

        Assert.True(toc.TotalTokens < full.TotalTokens);
        // The table of contents still reports the per-file cost of a full fetch.
        Assert.NotEmpty(toc.EmittedFileTokens);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }
}
