using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests.Enrichment;

// Near-duplicate member-body deduplication (roadmap 3.3), end to end through the default regex tier.
public sealed class BodyDeduplicatorTests : IDisposable
{
    private const string SharedBody = """
            {
                var sum = 0;
                foreach (var value in values) { sum += value; }
                var tag = "DEDUP-CANON-BODY-TOKEN";
                return sum + tag.Length;
            }
        """;

    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public BodyDeduplicatorTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-body-dedup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        WriteFile("Alpha.cs", $$"""
            public class Alpha
            {
                public int ComputeTotal(int[] values)
                {{SharedBody}}

                public int Unique()
                {
                    return 7 + "unique-alpha-body-token".Length;
                }
            }
            """);
        WriteFile("Beta.cs", $$"""
            public class Beta
            {
                public int ComputeTotal(int[] values)
                {{SharedBody}}
            }
            """);

        var services = new ServiceCollection();
        services.AddFuse();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_DeduplicateBodies_EmitsCanonicalOnceAndMarksDuplicate()
    {
        var output = await FuseAsync(deduplicateBodies: true);

        // The shared body's distinctive token survives exactly once (canonical kept, duplicate replaced).
        Assert.Equal(1, Occurrences(output, "DEDUP-CANON-BODY-TOKEN"));
        // A reference marker replaces the duplicate.
        Assert.Contains("fuse:body[", output);
        // Both signatures are preserved: the public API surface is never dropped.
        Assert.Equal(2, Occurrences(output, "public int ComputeTotal"));
    }

    [Fact]
    public async Task FuseAsync_DeduplicateBodies_LeavesUniqueBodyIntact()
    {
        var output = await FuseAsync(deduplicateBodies: true);

        // A member with a unique body is untouched.
        Assert.Contains("unique-alpha-body-token", output);
    }

    [Fact]
    public async Task FuseAsync_WithoutFlag_KeepsBothBodies()
    {
        var output = await FuseAsync(deduplicateBodies: false);

        // Without the flag both copies of the body are emitted in full.
        Assert.Equal(2, Occurrences(output, "DEDUP-CANON-BODY-TOKEN"));
        Assert.DoesNotContain("fuse:body[", output);
    }

    private async Task<string> FuseAsync(bool deduplicateBodies)
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(),
            new EmissionOptions { IncludeManifest = false, DeduplicateBodies = deduplicateBodies },
            inMemory: true);

        var result = await orchestrator.FuseAsync(request);
        Assert.NotNull(result.InMemoryContent);
        return result.InMemoryContent!;
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
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
