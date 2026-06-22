using Fuse.Fusion.Scoping;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Reduction;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

public sealed class FusionOrchestratorScopingTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorScopingTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-scoping-tests", Guid.NewGuid().ToString("N"));
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
            new ReductionOptions(level: ReductionLevel.Skeleton),
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

        Assert.Contains(exception.Errors, e => e.Contains("mutually exclusive"));
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

        var contentProvider = _serviceProvider.GetRequiredService<Func<ISourceContentProvider>>()();
        var reduced = await pipeline.ReduceAsync(
            [file],
            new ReductionOptions(level: ReductionLevel.Skeleton, includeSemanticMarkers: true),
            contentProvider);

        Assert.Single(reduced);
        var content = reduced[0].Content;
        Assert.StartsWith("<!-- fuse:type OrderSample", content);
        Assert.Contains("class OrderSample", content);
        Assert.DoesNotContain("secret-payload-abc", content);
        Assert.True(content.IndexOf("<!-- fuse:type", StringComparison.Ordinal) <
                    content.IndexOf("class OrderSample", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FuseAsync_WithQuery_ScopesToRelevantCluster()
    {
        WriteFile("PaymentService.cs", """
            public class PaymentService
            {
                public void ProcessPayment() {}
            }
            """);
        WriteFile("CatalogService.cs", """
            public class CatalogService
            {
                public void ListProducts() {}
            }
            """);

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true,
            query: new QueryOptions("payment process", TopFiles: 1, Depth: 1));

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("PaymentService.cs", result.InMemoryContent);
        Assert.DoesNotContain("CatalogService.cs", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_WithSecret_RedactsBeforeEmission()
    {
        WriteFile("Secrets.cs", """
            public class Secrets
            {
                public string Key = "AKIAIOSFODNN7EXAMPLE";
                public int Count = 1;
            }
            """);

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(enableRedaction: true),
            new EmissionOptions(),
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("[REDACTED:aws-access-key]", result.InMemoryContent);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result.InMemoryContent);
        Assert.Contains("Count = 1", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_WithRouteMap_PrependsRouteTable()
    {
        WriteFile("ApiController.cs", """
            [Route("api/items")]
            public class ItemsController
            {
                [HttpGet]
                public IActionResult List() => Ok();
            }
            """);

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(includeRouteMap: true),
            new EmissionOptions(),
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.StartsWith("<!-- fuse:route-map", result.InMemoryContent);
        Assert.Contains("GET", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_WithManifest_PrependsManifestHeader()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = true },
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.StartsWith("<!-- fuse:manifest", result.InMemoryContent);
        Assert.Contains("Seed.cs", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_WithGitStatsOutsideRepo_IncludesUnavailableNote()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = true, IncludeGitStats = true },
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("git: unavailable", result.InMemoryContent);
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
