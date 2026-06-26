using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// Exercises symbol-level packing end to end: a focused query keeps matched members in full and collapses the
// rest of the host type to signatures.
public sealed class FusionOrchestratorSymbolPackingTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorSymbolPackingTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-symbol-packing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        WriteFile("PaymentService.cs", """
            public class PaymentService
            {
                public void ProcessPayment()
                {
                    var token = "charge-token-marker";
                }

                public void Archive()
                {
                    var cold = "archive-storage-marker";
                }
            }
            """);

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_Query_KeepsMatchedMemberBody_CollapsesSibling()
    {
        var result = await FuseQueryAsync(new QueryOptions("process payment", TopFiles: 1, Depth: 1));

        Assert.NotNull(result.InMemoryContent);
        // The matched member is kept verbatim, body included.
        Assert.Contains("ProcessPayment", result.InMemoryContent);
        Assert.Contains("charge-token-marker", result.InMemoryContent);
        // The unmatched sibling is collapsed to a signature: its name survives, its body does not.
        Assert.Contains("Archive", result.InMemoryContent);
        Assert.DoesNotContain("archive-storage-marker", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_Query_SelectedMemberBodyStaysBraceBalanced()
    {
        var result = await FuseQueryAsync(new QueryOptions("process payment", TopFiles: 1, Depth: 1));

        Assert.NotNull(result.InMemoryContent);
        // The kept member must remain a balanced, parseable fragment.
        var content = result.InMemoryContent!;
        Assert.Equal(content.Count(c => c == '{'), content.Count(c => c == '}'));
    }

    [Fact]
    public async Task FuseAsync_Query_WithProvenance_ChainReferencesSelectedSymbol()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeProvenance = true },
            inMemory: true,
            query: new QueryOptions("process payment", TopFiles: 1, Depth: 1));

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        // Provenance references the selected member, not just the file.
        Assert.Contains("PaymentService.ProcessPayment", result.InMemoryContent);
    }

    private async Task<FusionResult> FuseQueryAsync(QueryOptions query)
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions(),
            inMemory: true,
            query: query);

        return await orchestrator.FuseAsync(request);
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
