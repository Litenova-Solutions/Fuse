using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Telemetry;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Tests.Engine;

/// <summary>
///     The caches behind a check and a test plan stay within their limits without changing an answer: the type walk
///     drops the facts of a document id a reload replaced, the reach cache drops the reference set unused the longest
///     past its limit, and the shadow maps hold no more after a second plan than after the first.
/// </summary>
public class MemoryBoundEngineTests
{
    [Fact]
    public async Task The_type_walk_drops_the_facts_of_a_document_id_a_reload_replaced()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var graph = engine.Workspace.Graph;
        var owners = graph.OwnersOf(engine.Repo.PathOf("Lib/Calc.cs")).ToList();
        await engine.Workspace.EnsureLoadedAsync(owners, TestContext.Current.CancellationToken);
        var dependents = owners.SelectMany(graph.DependentsOf).DistinctBy(p => p.Path)
            .Where(p => !owners.Any(o => o.Path == p.Path)).ToList();
        await engine.Workspace.EnsureLoadedAsync(dependents, TestContext.Current.CancellationToken);
        var reached = owners.Concat(dependents)
            .SelectMany(n => RepoWorkspace.ProjectsFor(engine.Workspace.Current, n)).ToList();
        var walk = new TypeWalk(engine.Workspace);

        Assert.Empty(await walk.SelectAsync(reached, [], TestContext.Current.CancellationToken));
        var documents = reached.Sum(p => p.Documents.Count());
        Assert.Equal(documents, walk.KeptFacts);

        var project = reached.First(p => p.Documents.Any(d => d.Name == "Calc.cs"));
        var document = project.Documents.First(d => d.Name == "Calc.cs");
        var text = await document.GetTextAsync(TestContext.Current.CancellationToken);
        var forked = engine.Workspace.Current.RemoveDocument(document.Id)
            .AddDocument(DocumentId.CreateNewId(project.Id), document.Name, text, document.Folders, document.FilePath);
        var reachedAgain = reached.Select(p => forked.GetProject(p.Id)!).ToList();

        Assert.Empty(await walk.SelectAsync(reachedAgain, [], TestContext.Current.CancellationToken));
        Assert.Equal(documents, walk.KeptFacts);
    }

    [Fact]
    public async Task The_reach_cache_keeps_at_most_its_limit_and_answers_the_same_again()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib/Many.cs", ManyMethods("M"));
        repo.Commit("add many methods");
        await using var engine = await InProcessEngine.StartAsync(repo);
        engine.Repo.Write("Lib/Many.cs", ManyMethods("N"));
        await engine.SyncAsync("Lib/Many.cs");
        var lib = engine.Workspace.Graph.Find(engine.Repo.PathOf("Lib/Lib.csproj"))!;
        await engine.Workspace.EnsureLoadedAsync([lib], TestContext.Current.CancellationToken);
        var reach = new ChangeReach(engine.Workspace);
        var path = engine.Repo.PathOf("Lib/Many.cs");

        var first = Assert.IsType<Reach.Precise>(await reach.ReachAsync([path], [lib], TestContext.Current.CancellationToken));

        Assert.Equal(ChangeReach.MaxCacheEntries, reach.CachedSets);
        var second = Assert.IsType<Reach.Precise>(await reach.ReachAsync([path], [lib], TestContext.Current.CancellationToken));
        Assert.Equal(ChangeReach.MaxCacheEntries, reach.CachedSets);
        Assert.Equal(first.Causes.Count, second.Causes.Count);
    }

    [Fact]
    public async Task The_shadow_maps_hold_no_more_after_a_second_plan_than_after_the_first()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.SyncAsync("Lib/Calc.cs");

        await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        var written = engine.Planner.Emitter.KeptWritten;
        var fingerprints = engine.Planner.Emitter.KeptFingerprints;
        await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.True(written > 0, "the first plan wrote no shadow file");
        Assert.True(fingerprints > 0, "the first plan recorded no fingerprint");
        Assert.Equal(written, engine.Planner.Emitter.KeptWritten);
        Assert.Equal(fingerprints, engine.Planner.Emitter.KeptFingerprints);
        Assert.True(written <= ShadowEmitter.MaxWrittenEntries);
        Assert.True(fingerprints <= ShadowEmitter.MaxFingerprintEntries);
    }

    private static string ManyMethods(string prefix) =>
        "namespace Lib;\n\npublic class Many\n{\n"
        + string.Join("\n", Enumerable.Range(0, ChangeReach.MaxCacheEntries + 4).Select(i => $"    public int {prefix}{i}(int a) => a + {i};"))
        + "\n}\n";
}
