using System.Text;
using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// Q5: member-level retrieval indexes each declared member and rolls per-member scores up to a file score, so a
// file whose match is concentrated in one member of an otherwise large file is surfaced even though whole-file
// length normalization ranks it outside the candidate pool. Opt-in via MemberLevelRetrieval.
public sealed class FusionOrchestratorMemberLevelTests : IDisposable
{
    private readonly string _sourceDirectory;

    public FusionOrchestratorMemberLevelTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-member", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        // Three small, high-density matches fill the file-granular candidate pool.
        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(_sourceDirectory, $"Small{i}.cs"),
                $"public class Small{i} {{ public string W = \"widget widget widget\"; }}");

        // A large class whose single relevant member mentions the query term once, diluted across many filler
        // members, so whole-file ranking pushes it below the candidate pool.
        var big = new StringBuilder("public class Big\n{\n    public string Relevant() => \"widget\";\n");
        for (var i = 0; i < 60; i++)
            big.Append($"    public int Filler{i}() => {i};\n");
        big.Append("}\n");
        File.WriteAllText(Path.Combine(_sourceDirectory, "Big.cs"), big.ToString());
    }

    [Fact]
    public async Task FuseAsync_MemberLevelOff_DoesNotSurfaceDilutedFile()
    {
        var emitted = await EmittedAsync(memberLevel: false);

        Assert.Contains("Small0.cs", emitted);
        Assert.DoesNotContain("Big.cs", emitted); // diluted match ranks outside the candidate pool
    }

    [Fact]
    public async Task FuseAsync_MemberLevelOn_SurfacesDilutedFileViaItsMember()
    {
        var emitted = await EmittedAsync(memberLevel: true);

        Assert.Contains("Big.cs", emitted); // its strong member match surfaces it as an extra seed
    }

    private async Task<IReadOnlyList<string>> EmittedAsync(bool memberLevel)
    {
        using var provider = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<FusionOrchestrator>();

        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(level: ReductionLevel.None),
            new EmissionOptions { IncludeManifest = false },
            inMemory: true,
            // A small pool (3) that the high-density small files fill, so Big.cs only appears via member-level.
            query: new QueryOptions("widget", TopFiles: 3, Depth: 1),
            experimental: new ExperimentalOptions
            {
                MemberLevelRetrieval = memberLevel,
                QueryExpansion = false,
                BudgetAwareExpansion = false,
                DowngradeBeforeDrop = false,
            });

        var result = await orchestrator.FuseAsync(request);
        Assert.NotNull(result.InMemoryContent);
        return System.Text.RegularExpressions.Regex.Matches(result.InMemoryContent!, "<file path=\"([^\"]+)\"")
            .Select(m => Path.GetFileName(m.Groups[1].Value))
            .ToList();
    }

    public void Dispose()
    {
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }
}
