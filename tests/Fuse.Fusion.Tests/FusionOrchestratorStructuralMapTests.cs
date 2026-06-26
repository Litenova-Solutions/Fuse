using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// P2: the route and project maps are an overview header for the whole output, so on multipart disk output they
// must prepend to the FIRST part, not the last where they would trail the content.
public sealed class FusionOrchestratorStructuralMapTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly string _outputDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorStructuralMapTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-structmap", Guid.NewGuid().ToString("N"));
        _outputDirectory = Path.Combine(_sourceDirectory, ".out");
        Directory.CreateDirectory(_sourceDirectory);

        WriteFile("ItemsController.cs", """
            [Route("api/items")]
            public class ItemsController
            {
                [HttpGet]
                public IActionResult List() => Ok();
            }
            """);
        // Filler files so a small split threshold forces multiple output parts.
        for (var i = 0; i < 6; i++)
        {
            WriteFile($"Filler{i}.cs", $$"""
                public class Filler{{i}}
                {
                    public string Value{{i}}() => "filler-body-content-for-splitting-{{i}}";
                }
                """);
        }

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_MultipartWithRouteMap_PrependsMapToFirstPart()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(includeRouteMap: true),
            new EmissionOptions
            {
                IncludeManifest = false,
                OutputDirectory = _outputDirectory,
                Overwrite = true,
                SplitTokens = 120,
            },
            inMemory: false);

        var result = await orchestrator.FuseAsync(request);

        Assert.True(result.GeneratedPaths.Count > 1, "expected multipart output");

        var partFiles = result.GeneratedPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var firstPart = partFiles.First(p => p.Contains("_part1", StringComparison.OrdinalIgnoreCase));
        var firstContent = File.ReadAllText(firstPart);

        // The route map heads the first part...
        Assert.StartsWith("<!-- fuse:route-map", firstContent);
        // ...and appears exactly once across all parts (not duplicated onto another part).
        var occurrences = partFiles.Sum(p =>
            File.ReadAllText(p).Split("fuse:route-map").Length - 1);
        Assert.Equal(1, occurrences);
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
