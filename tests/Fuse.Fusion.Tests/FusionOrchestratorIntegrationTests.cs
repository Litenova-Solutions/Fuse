using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

public sealed class FusionOrchestratorIntegrationTests : IDisposable
{
    private readonly string _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-fusion-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorIntegrationTests()
    {
        Directory.CreateDirectory(_sourceDirectory);
        File.WriteAllText(
            Path.Combine(_sourceDirectory, "Program.cs"),
            """
            namespace Sample;

            public static class Program
            {
                public static string Greet() => "hello";
            }
            """);
        File.WriteAllText(
            Path.Combine(_sourceDirectory, "config.json"),
            """
            {
                "name": "sample"
            }
            """);

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_WithSampleFiles_ProducesInMemoryOutput()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs", ".json"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("Program.cs", result.InMemoryContent);
        Assert.Contains("config.json", result.InMemoryContent);
        Assert.Equal(2, result.ProcessedFileCount);
        Assert.True(result.TotalTokens > 0);
    }

    [Fact]
    public async Task FuseAsync_ConcurrentCalls_AllSucceedWithIdenticalOutput()
    {
        // The orchestrator is a singleton that mutates shared per-run state on its injected singletons
        // (the content cache and the BM25 index). Concurrent calls must be serialized by the run gate; before
        // that guard this loop intermittently threw or returned corrupted output.
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        FusionRequest NewRequest() => new(
            new CollectionOptions(_sourceDirectory, extensions: [".cs", ".json"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true);

        var tasks = Enumerable.Range(0, 32).Select(_ => orchestrator.FuseAsync(NewRequest())).ToArray();
        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            Assert.NotNull(result.InMemoryContent);
            Assert.Equal(2, result.ProcessedFileCount);
        }

        Assert.Single(results.Select(r => r.InMemoryContent).Distinct());
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }
}
