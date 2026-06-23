using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

public sealed class FusionOrchestratorReviewTests : IDisposable
{
    private readonly string _repoDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorReviewTests()
    {
        _repoDirectory = Path.Combine(Path.GetTempPath(), "fuse-review-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoDirectory);

        File.WriteAllText(Path.Combine(_repoDirectory, "Order.cs"),
            "public class Order\n{\n    public int Id;\n}");
        File.WriteAllText(Path.Combine(_repoDirectory, "OrderService.cs"),
            "public class OrderService\n{\n    private Order _order;\n}");

        RunGit("init");
        RunGit("config user.email test@test.com");
        RunGit("config user.name Test");
        RunGit("add .");
        RunGit("commit -m baseline");

        // Change Order.cs so the diff has hunks.
        File.WriteAllText(Path.Combine(_repoDirectory, "Order.cs"),
            "public class Order\n{\n    public int Id { get; set; }\n    public string Name { get; set; }\n}");

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_ReviewMode_PrependsDiffHunksAndPairsCallers()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_repoDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true,
            changes: new ChangeOptions("HEAD", IncludeDependents: true, Review: true));

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        var content = result.InMemoryContent!;
        Assert.Contains("fuse:review", content);
        Assert.Contains("=== review: Order.cs", content);
        Assert.Contains("public string Name", content); // a diff hunk line
        Assert.Contains("OrderService.cs", content);     // the direct caller
        // The review map precedes the manifest and file bodies.
        Assert.True(content.IndexOf("fuse:review", StringComparison.Ordinal) <
                    content.IndexOf("<file path", StringComparison.Ordinal));
    }

    private void RunGit(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = _repoDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        try
        {
            if (Directory.Exists(_repoDirectory))
                Directory.Delete(_repoDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
