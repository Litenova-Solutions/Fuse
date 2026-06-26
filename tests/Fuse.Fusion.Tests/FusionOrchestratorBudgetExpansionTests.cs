using Fuse.Collection.Options;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Extensions;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Fusion.Tests;

// Item 4: budget-aware expansion. When a token ceiling is set, query-path dependency expansion admits
// neighbours highest-score first only while their estimated cost fits the budget, instead of admitting the
// whole neighbourhood for the packer to cut. Seeds are always admitted; gating is a no-op when the budget is
// ample, so it never drops a file the unbounded path would have emitted.
public sealed class FusionOrchestratorBudgetExpansionTests : IDisposable
{
    private readonly string _sourceDirectory;
    private readonly ServiceProvider _serviceProvider;

    public FusionOrchestratorBudgetExpansionTests()
    {
        _sourceDirectory = Path.Combine(Path.GetTempPath(), "fuse-budget-expansion", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDirectory);

        // SeedWidget is the query seed; it references four helpers, so a depth-1 expansion pulls them all in as
        // neighbours. The helpers are deliberately large so a tight budget cannot admit all of them.
        WriteFile("SeedWidget.cs", """
            public class SeedWidget
            {
                private readonly Helper1 _h1 = new();
                private readonly Helper2 _h2 = new();
                private readonly Helper3 _h3 = new();
                private readonly Helper4 _h4 = new();
                public void RunSeedWidget() { }
            }
            """);

        for (var i = 1; i <= 4; i++)
            WriteFile($"Helper{i}.cs", BigClass($"Helper{i}"));

        var services = new ServiceCollection();
        services.AddFuseForTests();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task FuseAsync_TightBudget_KeepsSeedAndAdmitsFewerThanUnbounded()
    {
        // With no ceiling the whole neighbourhood is admitted: the seed plus all four helpers.
        var unbounded = await EmittedFilesAsync(maxTokens: null);
        Assert.Contains("SeedWidget.cs", unbounded);
        Assert.Equal(5, unbounded.Count);

        // With a tight ceiling and gating on, the seed is still admitted but the budget cannot fit every helper.
        var gated = await EmittedFilesAsync(maxTokens: 500);
        Assert.Contains("SeedWidget.cs", gated);
        Assert.True(gated.Count < unbounded.Count, $"expected gating to admit fewer than {unbounded.Count}, got {gated.Count}");
    }

    [Fact]
    public async Task FuseAsync_AmpleBudget_GatingIsNeutral()
    {
        // A budget large enough for the whole set: gating on and off must emit the same files, so budget-aware
        // expansion never costs a file the unbounded path would have kept.
        var on = await EmittedFilesAsync(maxTokens: 100_000, budgetAware: true);
        var off = await EmittedFilesAsync(maxTokens: 100_000, budgetAware: false);

        Assert.Equal(off, on);
        Assert.Equal(5, on.Count);
    }

    private async Task<IReadOnlyList<string>> EmittedFilesAsync(int? maxTokens, bool budgetAware = true)
    {
        var orchestrator = _serviceProvider.GetRequiredService<FusionOrchestrator>();
        var request = new FusionRequest(
            new CollectionOptions(_sourceDirectory, extensions: [".cs"]),
            new ReductionOptions(level: ReductionLevel.None),
            new EmissionOptions { MaxTokens = maxTokens, IncludeManifest = false },
            inMemory: true,
            query: new QueryOptions("seed widget", TopFiles: 1, Depth: 1),
            // Tiering off keeps neighbour costs full-body and predictable, isolating the budget gate.
            experimental: new ExperimentalOptions { TieredEmission = false, BudgetAwareExpansion = budgetAware });

        var result = await orchestrator.FuseAsync(request);
        Assert.NotNull(result.InMemoryContent);
        return System.Text.RegularExpressions.Regex.Matches(result.InMemoryContent!, "<file path=\"([^\"]+)\"")
            .Select(m => Path.GetFileName(m.Groups[1].Value))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    // A class large enough that one full body consumes a meaningful slice of a tight token budget.
    private static string BigClass(string name)
    {
        var body = string.Join("\n", Enumerable.Range(0, 30)
            .Select(i => $"        var {name.ToLowerInvariant()}Field{i} = \"{name}-body-padding-value-{i}\";"));
        return $$"""
            public class {{name}}
            {
                public void Run()
                {
            {{body}}
                }
            }
            """;
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
