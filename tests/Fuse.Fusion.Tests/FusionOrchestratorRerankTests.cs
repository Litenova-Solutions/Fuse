using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// Item 9: the orchestrator reranks the query candidate pool through an optional IReranker. These tests use a
// stub reranker (no model) to prove the wiring: when dense rerank is on, the reranker chooses the seeds from a
// widened pool; when off or absent, the lexical order stands (the no-model floor).
public sealed class FusionOrchestratorRerankTests : IDisposable
{
    private readonly string _sourceDirectory;

    public FusionOrchestratorRerankTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-rerank", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        // Three independent files matching "widget" with decreasing term frequency, so BM25 ranks them
        // Top > Mid > Low. No dependency edges, so the emitted set is exactly the chosen seeds.
        WriteFile("Top.cs", "public class Top { public string W = \"widget widget widget widget\"; }");
        WriteFile("Mid.cs", "public class Mid { public string W = \"widget widget\"; }");
        WriteFile("Low.cs", "public class Low { public string W = \"widget\"; }");
    }

    [Fact]
    public async Task FuseAsync_RerankOff_KeepsLexicalTopSeed()
    {
        var emitted = await EmittedFilesAsync(rerank: false, reranker: new ReversingReranker());

        // Lexical order wins: the highest term-frequency file is the single seed.
        Assert.Contains("Top.cs", emitted);
        Assert.DoesNotContain("Low.cs", emitted);
    }

    [Fact]
    public async Task FuseAsync_RerankOn_RerankerChoosesSeedFromWidenedPool()
    {
        var emitted = await EmittedFilesAsync(rerank: true, reranker: new ReversingReranker());

        // The reranker reverses the widened candidate pool, so the lexically last file becomes the seed.
        Assert.Contains("Low.cs", emitted);
        Assert.DoesNotContain("Top.cs", emitted);
    }

    [Fact]
    public async Task FuseAsync_RerankOn_UnavailableReranker_KeepsLexicalOrder()
    {
        // An unavailable reranker (its model failed to load) must not change the lexical result.
        var emitted = await EmittedFilesAsync(rerank: true, reranker: new ReversingReranker { Available = false });

        Assert.Contains("Top.cs", emitted);
        Assert.DoesNotContain("Low.cs", emitted);
    }

    private async Task<IReadOnlyList<string>> EmittedFilesAsync(bool rerank, IReranker reranker)
    {
        var services = new ServiceCollection().AddFuseForTests();
        services.AddSingleton(reranker);
        using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<FusionOrchestrator>();

        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(level: ReductionLevel.None),
            new EmissionOptions { IncludeManifest = false },
            inMemory: true,
            // One seed, so the reranker's choice of which candidate becomes the seed is decisive.
            query: new QueryOptions("widget", TopFiles: 1, Depth: 1),
            experimental: new ExperimentalOptions { DenseRerank = rerank, QueryExpansion = false });

        var result = await orchestrator.FuseAsync(request);
        Assert.NotNull(result.InMemoryContent);
        return System.Text.RegularExpressions.Regex.Matches(result.InMemoryContent!, "<file path=\"([^\"]+)\"")
            .Select(m => Path.GetFileName(m.Groups[1].Value))
            .ToList();
    }

    private void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_sourceDirectory, name), content);

    public void Dispose()
    {
        if (Directory.Exists(_sourceDirectory))
            Directory.Delete(_sourceDirectory, recursive: true);
    }

    // A stub reranker that reverses the candidate pool, so a test can tell whether the orchestrator applied it.
    private sealed class ReversingReranker : IReranker
    {
        public bool Available { get; init; } = true;

        public bool IsAvailable => Available;

        public IReadOnlyList<RankedFile> Rerank(
            string query,
            IReadOnlyList<RankedFile> candidates,
            IReadOnlyDictionary<string, string> documentText) =>
            candidates.Reverse().ToList();
    }
}
