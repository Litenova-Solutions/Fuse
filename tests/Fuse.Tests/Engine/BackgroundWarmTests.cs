using Fuse.Check;
using Fuse.Tests.Fixtures;
using Fuse.Workspace.Generations;
using Microsoft.CodeAnalysis;
namespace Fuse.Tests.Engine;

/// <summary>
///     The background warm indexes baseline documents for later reference searches, and the background
///     preloader extracts dependents in one session, so neither waits on the request path.
/// </summary>
public class BackgroundWarmTests
{
    [Fact]
    public async Task The_warm_indexes_baseline_documents_for_later_searches()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        Assert.Empty((await engine.CheckAsync("Lib/Calc.cs")).Errors);

        var ct = TestContext.Current.CancellationToken;
        var generation = engine.Workspace.PinGeneration();
        var lib = Assert.Single(generation.Graph.OwnersOf(engine.Repo.PathOf("Lib/Calc.cs")));
        var reach = new ChangeReach(engine.Workspace);
        var baseline = engine.Workspace.PinGeneration().Baseline;
        var projects = RepoWorkspace.ProjectsFor(baseline, lib).ToList();
        Assert.NotEmpty(projects);
        var searched = projects.Select(p => p.Id).ToHashSet();

        // Cold: nothing is indexed, so every baseline document of the project is a candidate.
        var all = projects.SelectMany(p => p.Documents).Count();
        Assert.True(all > 1);
        Assert.Equal(all, reach.TokenIndex.CandidateDocumentsSnapshot(baseline, ["Add"], [], searched).Count);

        await reach.WarmIndexAsync(baseline, projects, ct);

        // Warm: only the files mentioning the token are candidates.
        var warm = reach.TokenIndex.CandidateDocumentsSnapshot(baseline, ["Add"], [], searched);
        Assert.True(warm.Count < all);
        Assert.All(warm, d => Assert.Equal("Calc.cs", Path.GetFileName(d.FilePath)));
    }

    [Fact]
    public async Task The_warmed_index_is_saved_for_later_runs()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.CheckAsync("Lib/Calc.cs");

        var ct = TestContext.Current.CancellationToken;
        var generation = engine.Workspace.PinGeneration();
        var lib = Assert.Single(generation.Graph.OwnersOf(engine.Repo.PathOf("Lib/Calc.cs")));
        await engine.Checker.WarmSearchIndexAsync(lib, ct);
        engine.Checker.SaveSearchIndex();

        Assert.True(
            File.Exists(Path.Combine(engine.Repo.Root.SharedStateDirectory, "token-index", "baseline.bin")),
            "the warmed search index was not saved");
    }

    [Fact]
    public async Task The_preloader_extracts_dependents_in_one_session()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.CheckAsync("Lib/Calc.cs");

        var generation = engine.Workspace.PinGeneration();
        var lib = Assert.Single(generation.Graph.OwnersOf(engine.Repo.PathOf("Lib/Calc.cs")));
        var dependents = generation.Graph.DependentsOf(lib).ToList();
        Assert.NotEmpty(dependents);
        Assert.All(dependents, d => Assert.False(engine.Workspace.IsLoaded(d)));
        int Evaluated()
        {
            lock (engine.Log)
                return engine.Log.Count(l => l.Contains("blueprint evaluated", StringComparison.Ordinal));
        }

        // One extraction session covers the whole batch, and the loads open from it without extracting again.
        var before = Evaluated();
        await engine.Workspace.EnsureExtractedAsync(dependents, TestContext.Current.CancellationToken);
        Assert.Equal(before + 1, Evaluated());
        foreach (var dependent in dependents)
            await engine.Workspace.PreloadAsync(dependent, TestContext.Current.CancellationToken);
        Assert.Equal(before + 1, Evaluated());
        Assert.All(dependents, d => Assert.True(engine.Workspace.IsLoaded(d)));
    }
}
