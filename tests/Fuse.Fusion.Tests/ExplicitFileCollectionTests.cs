using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion.Extensions;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

public sealed class ExplicitFileCollectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fuse-explicit-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _serviceProvider;

    public ExplicitFileCollectionTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "A.cs"), "namespace N; public class A { }");
        File.WriteAllText(Path.Combine(_dir, "B.cs"), "namespace N; public class B { }");
        File.WriteAllText(Path.Combine(_dir, "C.cs"), "namespace N; public class C { }");

        var services = new ServiceCollection();
        services.AddFuse();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task ExplicitFiles_CollectsOnlyNamedFiles_AndSkipsMissing()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_dir, extensions: [".cs"])
            {
                // Mixes a relative path, an absolute path, and a missing path (which is skipped).
                ExplicitFiles = ["A.cs", Path.Combine(_dir, "C.cs"), "Ghost.cs"]
            },
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);

        Assert.Equal(2, result.ProcessedFileCount);
        Assert.Contains("A.cs", result.InMemoryContent);
        Assert.Contains("C.cs", result.InMemoryContent);
        Assert.DoesNotContain("B.cs", result.InMemoryContent);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }
}
