using System.Diagnostics;
using Fuse.Paths;
using Fuse.Tests.Fixtures;
using Fuse.Workspace;
using Microsoft.CodeAnalysis;

namespace Fuse.Tests.Engine;

public class HypervisorMemoryTests
{
    [Fact]
    public async Task Shared_hypervisor_keys_pipe_and_mutex_by_git_common_dir_and_isolates_separate_repos()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var wt2Dir = Path.Combine(Path.GetTempPath(), "fuse-wt-" + Guid.NewGuid().ToString("N"));
        try
        {
            RunGit(engine.Repo.Root.Path, "worktree", "add", "--detach", wt2Dir);
            var wt1 = engine.Repo.Root;
            var wt2 = RepoRoot.Find(wt2Dir);

            Assert.NotNull(wt2);
            Assert.Equal(wt1.GitCommonDirectory, wt2.GitCommonDirectory);
            Assert.Equal(wt1.PipeName, wt2.PipeName);
            Assert.Equal(wt1.EnginePipeName, wt2.EnginePipeName);
            Assert.Equal(wt1.EngineMutexName, wt2.EngineMutexName);
            Assert.Equal(wt1.SharedStateDirectory, wt2.SharedStateDirectory);
            Assert.NotEqual(wt1.StateDirectory, wt2.StateDirectory);

            using var separate = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["Lib/Calc.cs"] = "public class Calc {}\n" });
            Assert.NotEqual(wt1.PipeName, separate.Root.PipeName);
            Assert.NotEqual(wt1.EngineMutexName, separate.Root.EngineMutexName);
            Assert.NotEqual(wt1.SharedStateDirectory, separate.Root.SharedStateDirectory);
        }
        finally
        {
            CleanupWorktree(engine.Repo.Root.Path, wt2Dir);
        }
    }

    [Fact]
    public async Task Zero_restore_secondary_worktree_maps_onto_restored_blueprint_and_shares_immutable_base_resources()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var engine = await InProcessEngine.StartAsync();
        Assert.Empty((await engine.CheckAsync("App/Report.cs")).Errors);

        var wt2Dir = Path.Combine(Path.GetTempPath(), "fuse-wt-zr-" + Guid.NewGuid().ToString("N"));
        RepoRoot? wt2Root = null;
        try
        {
            RunGit(engine.Repo.Root.Path, "worktree", "add", "--detach", wt2Dir);
            wt2Root = RepoRoot.Find(wt2Dir)!;
            Assert.False(File.Exists(Path.Combine(wt2Root.Path, "App", "obj", "project.assets.json")));
            Assert.False(File.Exists(Path.Combine(wt2Root.Path, "Lib", "obj", "project.assets.json")));

            var wt2Overlay = await engine.Workspace.RegisterWorktreeAsync(wt2Root, cancellationToken);
            var wt2Report = Path.Combine(wt2Root.Path, "App", "Report.cs");
            File.WriteAllText(wt2Report, "public class Broken { void M() { MissingSymbolCall(); } }\n");

            await wt2Overlay.SyncAndPinAsync([wt2Root.PathOf("App/Report.cs")], cancellationToken);
            var wt2App = wt2Overlay.Graph.Projects.Single(p => p.Name == "App");
            var wt2Gen = await wt2Overlay.EnsureLoadedAndPinAsync([wt2App], cancellationToken);

            Assert.True(wt2Overlay.IsZeroRestore);
            Assert.Equal(1, wt2Overlay.DeltaCount);
            Assert.Equal(0, engine.Workspace.PrimaryOverlay.DeltaCount);
            Assert.True(wt2Overlay.TryGetDelta(wt2Root.PathOf("App/Report.cs"), out var deltaText));
            Assert.Contains("MissingSymbolCall", deltaText!.ToString(), StringComparison.Ordinal);

            // Both worktree views share exact immutable MetadataReference instances from the HEAD base layer.
            var wt1App = RepoWorkspace.ProjectsFor(engine.Workspace.Current, engine.Workspace.Graph.Projects.Single(p => p.Name == "App")).Single();
            var wt2AppProject = RepoWorkspace.ProjectsFor(wt2Gen.Current, wt2App).Single();
            Assert.Same(wt1App.MetadataReferences[0], wt2AppProject.MetadataReferences[0]);

            var compilation = await wt2AppProject.GetCompilationAsync(cancellationToken);
            Assert.NotNull(compilation);
            Assert.Contains(compilation.GetDiagnostics(cancellationToken), d => d.Id == "CS0103");

            // Primary worktree stays completely unaffected.
            Assert.Empty((await engine.CheckAsync("App/Report.cs")).Errors);
        }
        finally
        {
            if (wt2Root is not null)
                engine.Workspace.UnregisterWorktree(wt2Root);
            CleanupWorktree(engine.Repo.Root.Path, wt2Dir);
        }
    }

    [Fact]
    public async Task Shedding_head_compilations_and_idle_projects_does_not_thrash_on_subsequent_edits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("App/Report.cs", "\"value=\" +", "\"value: \" +");
        Assert.Empty((await engine.CheckAsync("App/Report.cs")).Errors);

        var tests = engine.Workspace.Graph.Projects.Single(p => p.Name == "App.Tests");
        await engine.Workspace.EnsureLoadedAsync([tests], cancellationToken);
        Assert.True(engine.Workspace.IsLoaded(tests));

        await engine.Workspace.ClearHeadCompilationsAsync(cancellationToken);
        Assert.Empty(engine.Workspace.Baseline.Projects);

        // Incremental patch after ClearHeadCompilations MUST NOT trigger a full RebuildAsync.
        engine.Repo.Replace("App/Report.cs", "\"value: \" +", "\"value=\" +");
        await engine.Workspace.PatchViewsAsync([engine.Repo.PathOf("App/Report.cs")], cancellationToken);
        Assert.Empty(engine.Workspace.Baseline.Projects);

        await engine.Workspace.ClearIdleConeProjectsAsync(cancellationToken);
        Assert.False(engine.Workspace.IsLoaded(tests));
        Assert.DoesNotContain(engine.Workspace.Current.Projects, p => p.Name == "App.Tests");

        // Subsequent check of App keeps App.Tests unloaded instead of rebuilding/reloading it.
        engine.Repo.Replace("App/Report.cs", "\"value=\" +", "\"value: \" +");
        Assert.Empty((await engine.CheckAsync("App/Report.cs")).Errors);
        Assert.False(engine.Workspace.IsLoaded(tests));
        Assert.DoesNotContain(engine.Workspace.Current.Projects, p => p.Name == "App.Tests");

        // Requesting App.Tests explicitly unsheds and reloads it cleanly.
        await engine.Workspace.EnsureLoadedAsync([tests], cancellationToken);
        Assert.True(engine.Workspace.IsLoaded(tests));
        Assert.Contains(engine.Workspace.Current.Projects, p => p.Name == "App.Tests");
    }

    [Fact]
    public async Task BuildTransientBaselineAsync_builds_from_git_blobs_and_YieldMemory_compacts_heap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("App/Report.cs", "\"value=\" +", "\"broken_marker\" + MissingHeadError() +");
        await engine.Workspace.SyncAndPinAsync([engine.Repo.PathOf("App/Report.cs")], cancellationToken);
        var app = engine.Workspace.Graph.Projects.Single(p => p.Name == "App");
        await engine.Workspace.EnsureLoadedAsync([app], cancellationToken);

        using (var transient = await engine.Workspace.BuildTransientBaselineAsync([app], cancellationToken))
        {
            var baselineApp = RepoWorkspace.ProjectsFor(transient.Solution, app).Single();
            var compilation = await baselineApp.GetCompilationAsync(cancellationToken);
            Assert.NotNull(compilation);
            Assert.DoesNotContain(compilation.GetDiagnostics(cancellationToken), d => d.Severity == DiagnosticSeverity.Error);
        }

        var beforeGc = GC.CollectionCount(GC.MaxGeneration);
        engine.Workspace.YieldMemory();
        Assert.Equal(0, engine.Workspace.SessionViewCount);
        Assert.True(GC.CollectionCount(GC.MaxGeneration) > beforeGc);
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {stderr}{stdout}");
    }

    private static void CleanupWorktree(string repoPath, string worktreeDir)
    {
        if (!Directory.Exists(worktreeDir))
            return;
        try
        {
            RunGit(repoPath, "worktree", "remove", "--force", worktreeDir);
        }
        catch
        {
            try
            {
                Directory.Delete(worktreeDir, recursive: true);
            }
            catch
            {
            }
        }
    }
}
