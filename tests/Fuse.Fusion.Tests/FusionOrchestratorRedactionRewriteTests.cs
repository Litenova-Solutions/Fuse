using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// C1 invariant: content rebuilt from raw source AFTER the reduction stage (thin skeleton on the query path,
// symbol slice on the focus path) must still pass the secret redactor. Reduction-stage redaction runs inside
// ContentReductionPipeline.ReduceAsync; a member body kept verbatim by a post-reduction rewrite would
// otherwise re-introduce a secret the normal path removes.
public sealed class FusionOrchestratorRedactionRewriteTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorRedactionRewriteTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-redaction-rewrite", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        // The query/focus matches ProcessPayment, so its body is kept verbatim by the thin-skeleton and slice
        // rewrites. The AWS access key in that body is the secret that must not survive.
        WriteFile("PaymentService.cs", """
            public class PaymentService
            {
                public void ProcessPayment()
                {
                    var apiKey = "AKIAIOSFODNN7EXAMPLE";
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
    public async Task FuseAsync_ThinSkeleton_RedactsSecretInKeptMemberBody()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(enableRedaction: true),
            new EmissionOptions(),
            inMemory: true,
            query: new QueryOptions("process payment", TopFiles: 1, Depth: 1));

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        // The matched member is kept (its name survives) but the secret in its body is redacted.
        Assert.Contains("ProcessPayment", result.InMemoryContent);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result.InMemoryContent);
        Assert.Contains("[REDACTED:aws-access-key]", result.InMemoryContent);
    }

    [Fact]
    public async Task FuseAsync_SymbolSlice_RedactsSecretInKeptMemberBody()
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(enableRedaction: true),
            new EmissionOptions(),
            inMemory: true,
            focus: new FocusOptions("PaymentService.ProcessPayment", 1));

        var result = await orchestrator.FuseAsync(request);

        Assert.NotNull(result.InMemoryContent);
        Assert.Contains("ProcessPayment", result.InMemoryContent);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", result.InMemoryContent);
        Assert.Contains("[REDACTED:aws-access-key]", result.InMemoryContent);
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
