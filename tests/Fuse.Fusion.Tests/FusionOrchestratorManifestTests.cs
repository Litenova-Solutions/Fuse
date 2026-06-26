using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

public sealed class FusionOrchestratorManifestTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorManifestTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);
        File.WriteAllText(Path.Combine(_sourceDirectory, "Sample.cs"), "public class Sample { }");

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_WithManifestEnabled_PrependsManifestHeader()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new Fuse.Collection.Options.CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = true },
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.StartsWith("<!-- fuse:manifest", result.InMemoryContent);
        Assert.Contains("Sample.cs", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_PropagatesEmittedFileTokensThroughResult()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new Fuse.Collection.Options.CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = true },
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.NotEmpty(result.EmittedFileTokens);
        Assert.Contains(result.EmittedFileTokens, f => f.Path.EndsWith("Sample.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FuseAsync_WithNoManifest_OmitsManifestHeader()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new Fuse.Collection.Options.CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = false },
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.DoesNotContain("fuse:manifest", result.InMemoryContent);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }
}
