using System.Collections.Concurrent;
using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Graph;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;

namespace Fuse.Tests.Engine;

/// <summary>
///     How the workspace follows a sync that it cannot patch: the engine log names the case and why it was chosen, the
///     projects that were loaded are open again afterwards, and the next check answers from the working tree.
/// </summary>
public class WorkspaceSyncTests
{
    [Fact]
    public async Task More_than_300_changed_files_reload_without_evaluating_and_reopen_the_loaded_projects()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new ConcurrentQueue<string>();
        using var workspace = new RepoWorkspace(repo.Root, log.Enqueue);
        await workspace.InitializeAsync(TestContext.Current.CancellationToken);
        var checker = new Checker(workspace);
        Assert.Empty((await CheckAsync(checker, repo, "Lib/Calc.cs")).Errors);
        var lib = workspace.Graph.Find(repo.PathOf("Lib/Lib.csproj"))!;
        Assert.True(workspace.IsLoaded(lib));
        var generation = workspace.ConfigurationGeneration;

        // Files a hook reports are counted whether or not they exist, so no burst of watcher events is needed.
        repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        var reported = Enumerable.Range(0, 301).Select(i => repo.PathOf($"Lib/Generated/G{i}.cs")).Append(repo.PathOf("Lib/Calc.cs")).ToList();
        await workspace.SyncAsync(reported, TestContext.Current.CancellationToken);

        Assert.Contains("reloading: Reload trigger=302 changed files", log);
        Assert.Single(log, l => l.StartsWith("evaluated ", StringComparison.Ordinal));
        Assert.Equal(generation + 1, workspace.ConfigurationGeneration);
        Assert.True(workspace.IsLoaded(lib));
        var report = await CheckAsync(checker, repo, "Lib/Calc.cs");
        Assert.Equal("CS0103", Assert.Single(report.Errors).Error.Id);
    }

    [Fact]
    public async Task A_moved_head_evaluates_the_projects_again_and_the_log_names_the_commit()
    {
        using var repo = FixtureRepo.CreateStandard();
        var log = new ConcurrentQueue<string>();
        using var workspace = new RepoWorkspace(repo.Root, log.Enqueue);
        await workspace.InitializeAsync(TestContext.Current.CancellationToken);
        var generation = workspace.ConfigurationGeneration;

        repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        repo.Commit("move HEAD");
        var head = FixtureRepo.Run(repo.Root.Path, "git", "rev-parse", "HEAD").Trim();
        await workspace.SyncAsync([], TestContext.Current.CancellationToken);

        Assert.Contains($"reloading: Reevaluate trigger=HEAD moved to {head[..12]}", log);
        Assert.Equal(2, log.Count(l => l.StartsWith("evaluated ", StringComparison.Ordinal)));
        Assert.Equal(generation + 1, workspace.ConfigurationGeneration);
    }

    [Fact]
    public async Task A_patch_keeps_the_configuration_generation()
    {
        await using var engine = await InProcessEngine.StartAsync();
        Assert.Empty((await engine.CheckAsync("Lib/Calc.cs")).Errors);
        var generation = engine.Workspace.ConfigurationGeneration;
        var baseline = engine.Workspace.BaselineGeneration;

        // A body edit changes the working tree only; the baseline and the project configuration stay as they were.
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.Workspace.SyncAsync([engine.Repo.PathOf("Lib/Calc.cs")], TestContext.Current.CancellationToken);

        Assert.Equal(generation, engine.Workspace.ConfigurationGeneration);
        Assert.Equal(baseline, engine.Workspace.BaselineGeneration);
    }

    [Fact]
    public async Task A_load_cut_short_after_a_project_opened_puts_that_project_in_both_views_at_the_next_request()
    {
        using var repo = FixtureRepo.CreateStandard();
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        // The request gives up once Lib is open and before Multi is, as a hook that stops waiting during a cold load does.
        using var workspace = new RepoWorkspace(repo.Root, message =>
        {
            if (message.StartsWith("loaded Lib ", StringComparison.Ordinal))
                cut.Cancel();
        });
        await workspace.InitializeAsync(TestContext.Current.CancellationToken);
        var lib = workspace.Graph.Find(repo.PathOf("Lib/Lib.csproj"))!;
        var multi = workspace.Graph.Find(repo.PathOf("Multi/Multi.csproj"))!;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.EnsureLoadedAsync([lib, multi], cut.Token));
        Assert.True(workspace.IsLoaded(lib));
        Assert.False(workspace.IsLoaded(multi));

        repo.Replace("Lib/Calc.cs", "a * b;", "a * undefinedValue;");
        var report = await CheckAsync(new Checker(workspace), repo, "Lib/Calc.cs");
        Assert.Equal("CS0103", Assert.Single(report.Errors).Error.Id);
    }

    [Fact]
    public async Task A_reload_cut_short_while_it_reopens_the_projects_leaves_the_reopened_one_in_both_views()
    {
        using var repo = FixtureRepo.CreateStandard();
        using var cut = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var armed = false;
        string? reopened = null;
        using var workspace = new RepoWorkspace(repo.Root, message =>
        {
            // The reopen goes through the loaded projects in no fixed order, so whichever opens first stops it.
            if (armed && reopened is null && message.StartsWith("loaded ", StringComparison.Ordinal))
            {
                reopened = message.Split(' ')[1];
                cut.Cancel();
            }
        });
        await workspace.InitializeAsync(TestContext.Current.CancellationToken);
        var checker = new Checker(workspace);
        Assert.Empty((await CheckAsync(checker, repo, "Lib/Calc.cs")).Errors);
        Assert.Empty((await CheckAsync(checker, repo, "Multi/Shape.cs")).Errors);

        armed = true;
        var reported = Enumerable.Range(0, 301).Select(i => repo.PathOf($"Lib/Generated/G{i}.cs")).ToList();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.SyncAsync(reported, cut.Token));

        Assert.True(reopened is "Lib" or "Multi", $"reopened {reopened}");
        var (file, before, after) = reopened == "Lib" ? ("Lib/Calc.cs", "a * b;", "a * undefinedValue;") : ("Multi/Shape.cs", "=> count;", "=> undefinedCount;");
        repo.Replace(file, before, after);
        var report = await CheckAsync(checker, repo, file);
        Assert.Equal("CS0103", Assert.Single(report.Errors).Error.Id);
    }

    [Fact]
    public async Task A_request_that_finds_a_project_a_background_load_just_opened_puts_it_in_both_views()
    {
        using var repo = FixtureRepo.CreateStandard();
        RepoWorkspace? workspace = null;
        ProjectNode? lib = null;
        var seen = new List<bool>();
        // The loader logs each project it opened while it still holds its load lock, after the project shows as loaded:
        // a request that runs at that moment finds the project open and loads nothing itself.
        using (workspace = new RepoWorkspace(repo.Root, message =>
               {
                   if (lib is null || !message.StartsWith("loaded Lib ", StringComparison.Ordinal))
                       return;
                   workspace!.EnsureLoadedAsync([lib], TestContext.Current.CancellationToken).GetAwaiter().GetResult();
                   seen.Add(RepoWorkspace.ProjectsFor(workspace.Current, lib).Any() && RepoWorkspace.ProjectsFor(workspace.Baseline, lib).Any());
               }))
        {
            await workspace.InitializeAsync(TestContext.Current.CancellationToken);
            lib = workspace.Graph.Find(repo.PathOf("Lib/Lib.csproj"))!;

            await workspace.PreloadAsync(lib, TestContext.Current.CancellationToken);

            Assert.Equal([true], seen);
        }
    }

    private static Task<CheckResult> CheckAsync(Checker checker, FixtureRepo repo, string relative) =>
        checker.CheckAsync(new CheckScope.Files([repo.PathOf(relative)]), PhaseTimes.None, TestContext.Current.CancellationToken);
}
