using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

/// <summary>
///     Verifies that the orchestrator runs fusions concurrently with no process-wide gate: the per-run content
///     cache and BM25 index hold no cross-run state, so simultaneous runs stay isolated and correct.
/// </summary>
public sealed class FusionConcurrencyTests : IDisposable
{
    private readonly List<string> _dirs = [];
    private readonly ServiceProvider _serviceProvider;

    public FusionConcurrencyTests()
    {
        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_ConcurrentRunsAgainstDifferentDirectories_AreIsolated()
    {
        const int runs = 8;

        // Each directory carries a unique marker token so cross-run leakage would be detectable.
        var requests = new List<(FusionRequest Request, string Marker)>();
        for (var i = 0; i < runs; i++)
        {
            var marker = $"UniqueMarker{i}xyz";
            var dir = NewDirectory();
            WriteFile(dir, "Service.cs", $$"""
                public class Service{{i}}
                {
                    public string Token() => "{{marker}}";
                }
                """);
            requests.Add((BuildQueryRequest(dir, $"Service{i}"), marker));
        }

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        var results = await Task.WhenAll(requests.Select(r => orchestrator.FuseAsync(r.Request)));

        for (var i = 0; i < runs; i++)
        {
            var content = results[i].InMemoryContent;
            Assert.NotNull(content);
            // Each run must contain its own marker and none of the others'.
            Assert.Contains(requests[i].Marker, content);
            for (var j = 0; j < runs; j++)
            {
                if (j != i)
                    Assert.DoesNotContain(requests[j].Marker, content);
            }
        }
    }

    [Fact]
    public async Task FuseAsync_ConcurrentRunsAgainstSameDirectory_ProduceConsistentResults()
    {
        var dir = NewDirectory();
        WriteFile(dir, "Alpha.cs", """
            public class Alpha
            {
                public string Name => "alpha-token";
            }
            """);
        WriteFile(dir, "Beta.cs", """
            public class Beta
            {
                public int Value => 42;
            }
            """);

        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();

        var tasks = Enumerable.Range(0, 12)
            .Select(_ => orchestrator.FuseAsync(BuildQueryRequest(dir, "Alpha")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // The per-run index must not collide: every concurrent run yields the same isolated, correct output.
        var first = results[0].InMemoryContent;
        Assert.NotNull(first);
        Assert.Contains("Alpha.cs", first);
        foreach (var result in results)
            Assert.Equal(first, result.InMemoryContent);
    }

    private static FusionRequest BuildQueryRequest(string dir, string query) =>
        new(
            new CollectionOptions(dir, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true,
            query: new QueryOptions(query));

    private string NewDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-concurrency-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private static void WriteFile(string dir, string name, string content) =>
        File.WriteAllText(Path.Combine(dir, name), content);

    public void Dispose()
    {
        _serviceProvider.Dispose();
        foreach (var dir in _dirs)
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
