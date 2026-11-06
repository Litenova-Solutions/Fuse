using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Tests.Engine;

/// <summary>
///     A generation is the state a check pins: the views, the graph and the changed files at one instant. A sync
///     or a load the writer rounds off publishes the next one, a round that changes nothing keeps the current
///     one, and a pinned generation keeps answering from its own views while the writer publishes ahead of it.
/// </summary>
public class GenerationTests
{
    [Fact]
    public async Task A_pinned_generation_keeps_its_views_while_a_sync_publishes_the_next_one()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await engine.CheckAsync("Lib/Calc.cs");
        var pinned = engine.Workspace.PinGeneration();
        var path = engine.Repo.PathOf("Lib/Calc.cs");

        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var next = await engine.Workspace.SyncAndPinAsync([path], TestContext.Current.CancellationToken);

        Assert.True(next.Number > pinned.Number);
        Assert.Contains(path, next.Changed);
        Assert.DoesNotContain("a * b + 1;", await TextOfAsync(pinned, engine, "Lib/Calc.cs"));
        Assert.Contains("a * b + 1;", await TextOfAsync(next, engine, "Lib/Calc.cs"));
        Assert.Same(next, engine.Workspace.PinGeneration());
    }

    [Fact]
    public async Task A_sync_that_changes_nothing_keeps_the_generation()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await engine.CheckAsync("Lib/Calc.cs");
        var pinned = engine.Workspace.PinGeneration();

        var again = await engine.Workspace.SyncAndPinAsync([], TestContext.Current.CancellationToken);

        Assert.Same(pinned, again);
        Assert.Same(pinned, engine.Workspace.PinGeneration());
    }

    [Fact]
    public async Task A_load_publishes_a_generation_that_holds_the_project_it_loaded()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var lib = engine.Workspace.Graph.Find(engine.Repo.PathOf("Lib/Lib.csproj"))!;
        var pinned = engine.Workspace.PinGeneration();
        Assert.Empty(RepoWorkspace.ProjectsFor(pinned.Current, lib));

        var next = await engine.Workspace.EnsureLoadedAndPinAsync([lib], TestContext.Current.CancellationToken);

        Assert.True(next.Number > pinned.Number);
        Assert.NotEmpty(RepoWorkspace.ProjectsFor(next.Current, lib));
        Assert.NotEmpty(RepoWorkspace.ProjectsFor(next.Baseline, lib));
        Assert.Empty(RepoWorkspace.ProjectsFor(pinned.Current, lib));
    }

    [Fact]
    public async Task Concurrent_syncs_publish_one_generation_for_the_edit_they_share()
    {
        await using var engine = await InProcessEngine.StartAsync();
        await engine.CheckAsync("Lib/Calc.cs");
        var before = engine.Workspace.PinGeneration();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        var path = engine.Repo.PathOf("Lib/Calc.cs");

        var generations = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            engine.Workspace.SyncAndPinAsync([path], TestContext.Current.CancellationToken)));

        var published = engine.Workspace.PinGeneration();
        Assert.True(published.Number > before.Number);
        Assert.All(generations, generation => Assert.True(generation.Number <= published.Number));
        Assert.Contains(path, published.Changed);
        Assert.Contains("a * b + 1;", await TextOfAsync(published, engine, "Lib/Calc.cs"));
    }

    /// <summary>The text of <paramref name="relative"/> as <paramref name="generation"/>'s current view holds it.</summary>
    private static async Task<string> TextOfAsync(Generation generation, InProcessEngine engine, string relative)
    {
        var ids = generation.Current.GetDocumentIdsWithFilePath(engine.Repo.Full(relative));
        var document = generation.Current.GetDocument(ids[0])!;
        return (await document.GetTextAsync(TestContext.Current.CancellationToken)).ToString();
    }
}
