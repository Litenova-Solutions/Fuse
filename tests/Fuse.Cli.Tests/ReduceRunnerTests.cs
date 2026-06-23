using Fuse.Cli;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Tests;

public sealed class ReduceRunnerTests : IDisposable
{
    private const string ClassWithBody = """
        namespace N;

        public class Sample
        {
            public int Add(int x, int y)
            {
                return x + y + 8675309;
            }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fuse-reduce-runner-tests", Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _serviceProvider;

    public ReduceRunnerTests()
    {
        Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        services.AddFuse();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task ReduceFilesAsync_ReducesOnlyTheNamedFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "A.cs"), ClassWithBody);
        await File.WriteAllTextAsync(Path.Combine(_dir, "Other.cs"), "namespace N; public class Other { }");
        var (orchestrator, registry) = Resolve();

        var output = await ReduceRunner.ReduceFilesAsync(
            orchestrator, registry, _dir, ["A.cs"], ReductionLevel.None, null, CancellationToken.None);

        Assert.Contains("A.cs", output);
        Assert.DoesNotContain("Other.cs", output);
    }

    [Fact]
    public async Task ReduceFilesAsync_SkeletonLevel_KeepsSignatureDropsBody()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "B.cs"), ClassWithBody);
        var (orchestrator, registry) = Resolve();

        var output = await ReduceRunner.ReduceFilesAsync(
            orchestrator, registry, _dir, ["B.cs"], ReductionLevel.Skeleton, null, CancellationToken.None);

        Assert.Contains("Add", output);          // signature survives
        Assert.DoesNotContain("8675309", output); // body is dropped
    }

    [Fact]
    public async Task ReduceContentAsync_ReducesRawContentSelectedByExtension()
    {
        var (orchestrator, registry) = Resolve();

        var output = await ReduceRunner.ReduceContentAsync(
            orchestrator, registry, ClassWithBody, ".cs", ReductionLevel.Skeleton, null, CancellationToken.None);

        Assert.Contains("input.cs", output);
        Assert.Contains("Add", output);
        Assert.DoesNotContain("8675309", output);
    }

    [Fact]
    public async Task ReduceFilesAsync_EmptyList_ReturnsError()
    {
        var (orchestrator, registry) = Resolve();

        var output = await ReduceRunner.ReduceFilesAsync(
            orchestrator, registry, _dir, [], ReductionLevel.None, null, CancellationToken.None);

        Assert.StartsWith("Error", output);
    }

    private (FusionOrchestrator Orchestrator, ProjectTemplateRegistry Registry) Resolve() =>
        (_serviceProvider.GetRequiredService<FusionOrchestrator>(),
         _serviceProvider.GetRequiredService<ProjectTemplateRegistry>());

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
