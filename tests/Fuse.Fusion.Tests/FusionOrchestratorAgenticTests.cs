using Fuse.Analysis.Changes;
using Fuse.Analysis.Dependencies;
using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Reduction;
using Fuse.Reduction.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

public sealed class FusionOrchestratorAgenticTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorAgenticTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-agentic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        WriteFile("Seed.cs", """
            public class Seed
            {
                public Dep Dependency { get; set; }
            }
            """);
        WriteFile("Dep.cs", """
            public class Dep
            {
                public int Id { get; set; }
            }
            """);
        WriteFile("Unrelated.cs", """
            public class Unrelated
            {
                public string Name => "standalone";
            }
            """);

        var services = new ServiceCollection();
        services.AddFuse();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_WithFocusSeed_ScopesToDependencies()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true,
            focus: new FocusOptions("Seed", 1));

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("Seed.cs", result.InMemoryContent);
        Assert.Contains("Dep.cs", result.InMemoryContent);
        Assert.DoesNotContain("Unrelated.cs", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_WithSkeleton_ProducesSignaturesOnly()
    {
        WriteFile("Body.cs", """
            public class BodySample
            {
                public string Run()
                {
                    return "unique-body-token-xyzzy";
                }
            }
            """);

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(skeletonMode: true),
            new EmissionOptions(),
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("BodySample", result.InMemoryContent);
        Assert.DoesNotContain("unique-body-token-xyzzy", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_WithSemanticMarkers_PrependsComments()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(includeSemanticMarkers: true),
            new EmissionOptions(),
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("<!-- fuse:type Seed", result.InMemoryContent);
        Assert.True(result.InMemoryContent.IndexOf("<!-- fuse:type", StringComparison.Ordinal) <
                    result.InMemoryContent.IndexOf("class Seed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FuseAsync_BothFocusAndChanges_ThrowsValidationException()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions(),
            focus: new FocusOptions("Seed"),
            changes: new ChangeOptions("HEAD"));

        var exception = await Assert.ThrowsAsync<FusionValidationException>(() => orchestrator.FuseAsync(request));

        Assert.Contains(exception.Errors, e => e.Contains("FocusOptions and ChangeOptions cannot both be set"));
    }

    [Fact]
    public async Task FuseAsync_FocusSeedNotFound_ThrowsValidationException()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true,
            focus: new FocusOptions("DoesNotExistAnywhere"));

        var exception = await Assert.ThrowsAsync<FusionValidationException>(() => orchestrator.FuseAsync(request));

        Assert.Contains(exception.Errors, e => e.Contains("Focus seed 'DoesNotExistAnywhere' matched no collected files."));
    }

    [Fact]
    public async Task FuseAsync_ChangedPathNotInCollection_ExcludesPath()
    {
        var services = new ServiceCollection();
        services.AddFuse();
        services.AddSingleton<IChangeDetector>(new StubChangeDetector(["Seed.cs", "Outside.py"]));
        await using var provider = services.BuildServiceProvider();

        var orchestrator = provider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true,
            changes: new ChangeOptions("HEAD", false));

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("Seed.cs", result.InMemoryContent);
        Assert.DoesNotContain("Outside.py", result.InMemoryContent);
        Assert.DoesNotContain("Dep.cs", result.InMemoryContent);
        Assert.DoesNotContain("Unrelated.cs", result.InMemoryContent);
    }

    [Fact]
    public async Task ContentReductionPipeline_SkeletonThenMarkers_OrderCorrect()
    {
        WriteFile("Order.cs", """
            public class OrderSample
            {
                public void Execute()
                {
                    var hidden = "secret-payload-abc";
                }
            }
            """);

        var pipeline = _serviceProvider.GetRequiredService<ContentReductionPipeline>();
        var file = new Fuse.Collection.Models.SourceFile(
            new Fuse.Collection.Models.FileCandidate(
                Path.Combine(_sourceDirectory, "Order.cs"),
                "Order.cs",
                new FileInfo(Path.Combine(_sourceDirectory, "Order.cs"))));

        var reduced = await pipeline.ReduceAsync(
            [file],
            new ReductionOptions(skeletonMode: true, includeSemanticMarkers: true));

        Assert.Single(reduced);
        var content = reduced[0].Content;
        Assert.StartsWith("<!-- fuse:type OrderSample", content);
        Assert.Contains("class OrderSample", content);
        Assert.DoesNotContain("secret-payload-abc", content);
        Assert.True(content.IndexOf("<!-- fuse:type", StringComparison.Ordinal) <
                    content.IndexOf("class OrderSample", StringComparison.Ordinal));
    }

    private void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_sourceDirectory, name), content);

    public void Dispose()
    {
        _serviceProvider.Dispose();
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }

    private sealed class StubChangeDetector(IReadOnlyList<string> paths) : IChangeDetector
    {
        public Task<IReadOnlyList<string>> GetChangedRelativePathsAsync(
            string sourceDirectory,
            string since,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(paths);
    }
}
