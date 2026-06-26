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
        services.AddFuseForTests();
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

    // A body shared by an overload across two files, long enough to qualify for deduplication.
    private const string OverloadSharedBody = """
            {
                var sum = 0;
                for (var i = 0; i < x; i++) { sum += i; }
                var tag = "OVERLOAD-SHARED-BODY-TOKEN-DISTINCT";
                return sum + tag.Length;
            }
        """;

    [Fact]
    public async Task FuseAsync_DeduplicateBodies_DoesNotCollapseSiblingOverloadWithUniqueBody()
    {
        // C5: GammaCanon.Process(int) and GammaDup.Process(int) share a body (deduplicated). GammaDup also
        // declares an overload Process(int, int) with a unique body. Keying the rewrite on the display name
        // would collapse BOTH overloads in GammaDup (they share Type.Member); keying on the collision-free
        // identity collapses only the duplicated overload and leaves the unique one intact.
        WriteFile("GammaCanon.cs", $$"""
            public class GammaCanon
            {
                public int Process(int x)
                {{OverloadSharedBody}}
            }
            """);
        WriteFile("GammaDup.cs", $$"""
            public class GammaDup
            {
                public int Process(int x)
                {{OverloadSharedBody}}

                public int Process(int x, int y)
                {
                    var marker = "gamma-unique-overload-token-distinct";
                    return x + y + marker.Length;
                }
            }
            """);

        var output = await FuseAsync(deduplicateBodies: true);

        // The duplicate single-arg overload is collapsed (a marker is emitted)...
        Assert.Contains("fuse:body[", output);
        // ...but the two-arg overload's unique body must survive: identity keying does not conflate overloads.
        Assert.Contains("gamma-unique-overload-token-distinct", output);
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
