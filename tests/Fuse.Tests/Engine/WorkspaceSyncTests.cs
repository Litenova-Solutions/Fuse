using System.Collections.Concurrent;
using Fuse.Check;
using Fuse.Check.Model;
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

    private static Task<CheckResult> CheckAsync(Checker checker, FixtureRepo repo, string relative) =>
        checker.CheckAsync(new CheckScope.Files([repo.PathOf(relative)]), PhaseTimes.None, TestContext.Current.CancellationToken);
}
