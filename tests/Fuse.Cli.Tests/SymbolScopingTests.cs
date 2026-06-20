using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Languages.CSharp.Roslyn.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Tests;

public sealed class SymbolScopingTests : IDisposable
{
    private readonly string _sourceDirectory;

    public SymbolScopingTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-symbol-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);
        File.WriteAllText(Path.Combine(_sourceDirectory, "OrderService.cs"), """
            public class OrderService
            {
                public void Charge()
                {
                    var receipt = 1234;
                }

                public void Refund()
                {
                    var refundAmount = 9999;
                }
            }
            """);
    }

    [Fact]
    public async Task FuseAsync_FocusOnTypeDotMember_EmitsOnlyThatMemberBody()
    {
        var provider = new ServiceCollection().AddFuse().AddCSharpRoslyn().BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<FusionOrchestrator>();

        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = false },
            inMemory: true,
            focus: new FocusOptions("OrderService.Charge"));

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("var receipt = 1234", result.InMemoryContent); // target member body kept
        Assert.Contains("Refund", result.InMemoryContent);             // sibling signature kept
        Assert.DoesNotContain("var refundAmount = 9999", result.InMemoryContent); // sibling body stripped
    }

    public void Dispose()
    {
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }
}
