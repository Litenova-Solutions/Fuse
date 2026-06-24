using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Enrichment;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// Q6: the git churn prior multiplies a candidate's score by its normalized recent churn, behind a weight that
// is zero by default. These tests use a stub git stats provider (deterministic synthetic churn) to prove the
// prior reorders candidates when on and is inert when off.
public sealed class FusionOrchestratorChurnTests : IDisposable
{
    private readonly string _sourceDirectory;

    public FusionOrchestratorChurnTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-churn", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        // Three files with equal lexical relevance to "widget", so the lexical tie breaks by path (A before C).
        // Churn then decides which one wins the single seed slot.
        WriteFile("A.cs", "public class A { public string W = \"widget\"; }");
        WriteFile("B.cs", "public class B { public string W = \"widget\"; }");
        WriteFile("C.cs", "public class C { public string W = \"widget\"; }");
    }

    [Fact]
    public async Task FuseAsync_ChurnOff_BreaksLexicalTieByPath()
    {
        // No churn prior: equal scores, so the alphabetically first path is the seed.
        var emitted = await EmittedFilesAsync(churnWeight: 0, churnyPath: "C.cs");

        Assert.Contains("A.cs", emitted);
        Assert.DoesNotContain("C.cs", emitted);
    }

    [Fact]
    public async Task FuseAsync_ChurnOn_PromotesHighChurnCandidate()
    {
        // With a churn prior and C.cs the recently changed file, churn breaks the tie in C's favor.
        var emitted = await EmittedFilesAsync(churnWeight: 1.0, churnyPath: "C.cs");

        Assert.Contains("C.cs", emitted);
        Assert.DoesNotContain("A.cs", emitted);
    }

    private async Task<IReadOnlyList<string>> EmittedFilesAsync(double churnWeight, string churnyPath)
    {
        var services = new ServiceCollection().AddFuseForTests();
        services.AddSingleton<IGitStatsProvider>(new StubGitStatsProvider(churnyPath));
        using var provider = services.BuildServiceProvider();
        var orchestrator = provider.GetRequiredService<FusionOrchestrator>();

        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(level: ReductionLevel.None),
            new EmissionOptions { IncludeManifest = false },
            inMemory: true,
            query: new QueryOptions("widget", TopFiles: 1, Depth: 1),
            experimental: new ExperimentalOptions { GitChurnWeight = churnWeight, QueryExpansion = false });

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

    // Reports a single recently changed file with high churn and all others with none.
    private sealed class StubGitStatsProvider(string churnyPath) : IGitStatsProvider
    {
        public Task<GitStatsResult> GetStatsAsync(
            string sourceDirectory,
            IReadOnlyList<string> relativePaths,
            CancellationToken cancellationToken = default)
        {
            var stats = relativePaths.ToDictionary(
                p => p,
                p => new GitFileStats(p, p.EndsWith(churnyPath, StringComparison.OrdinalIgnoreCase) ? 10 : 0, null),
                StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new GitStatsResult(true, stats));
        }
    }
}
