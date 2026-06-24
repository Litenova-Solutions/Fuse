using System.Text;
using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// P1: downgrade-before-drop. Under a token budget, the lower-relevance tail that would be dropped is replaced
// with a compact sketch so it stays present. Recall counts file presence, so this targets multi-file
// truncation. Opt-in via DowngradeBeforeDrop.
public sealed class FusionOrchestratorDowngradeTests : IDisposable
{
    private readonly string _sourceDirectory;

    public FusionOrchestratorDowngradeTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-downgrade", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        // Seed matches the query strongly and is small (admitted full). Big matches weakly and is huge, so it
        // is the low-relevance tail that overflows a tight budget.
        File.WriteAllText(Path.Combine(_sourceDirectory, "Seed.cs"),
            "public class Seed { public string A = \"alpha alpha alpha alpha alpha\"; }");

        var big = new StringBuilder("public class Big\n{\n    public string A = \"alpha\";\n");
        for (var i = 0; i < 1200; i++)
            big.Append($"    public int Method{i}() => {i};\n");
        big.Append("    public string Body() => \"big-body-marker\";\n}\n");
        File.WriteAllText(Path.Combine(_sourceDirectory, "Big.cs"), big.ToString());
    }

    [Fact]
    public async Task FuseAsync_DowngradeOff_DropsOverflowTail()
    {
        var emitted = await EmittedAsync(downgrade: false);

        Assert.Contains("class Seed", emitted);
        // Big overflows the budget and is dropped: neither its body nor a sketch appears.
        Assert.DoesNotContain("big-body-marker", emitted);
        Assert.DoesNotContain("fuse:sketch", emitted);
    }

    [Fact]
    public async Task FuseAsync_DowngradeOn_KeepsTailAsSketch()
    {
        var emitted = await EmittedAsync(downgrade: true);

        Assert.Contains("class Seed", emitted);
        // Big stays present as a sketch: its type survives, its body does not.
        Assert.Contains("class Big", emitted);
        Assert.Contains("fuse:sketch", emitted);
        Assert.DoesNotContain("big-body-marker", emitted);
    }

    private async Task<string> EmittedAsync(bool downgrade)
    {
        using var provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<FusionOrchestrator>();

        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(level: ReductionLevel.None),
            new EmissionOptions { MaxTokens = 2000, IncludeManifest = false },
            inMemory: true,
            query: new QueryOptions("alpha", TopFiles: 10, Depth: 1),
            experimental: new ExperimentalOptions { DowngradeBeforeDrop = downgrade, QueryExpansion = false });

        var result = await orchestrator.FuseAsync(request);
        Assert.NotNull(result.InMemoryContent);
        return result.InMemoryContent!;
    }

    public void Dispose()
    {
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }
}
