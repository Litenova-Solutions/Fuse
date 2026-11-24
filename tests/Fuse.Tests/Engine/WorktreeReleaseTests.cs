using System.Diagnostics;
using Fuse.Engine;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Releasing a secondary worktree closes its watcher and its git reader so the directory deletes, while the
///     primary keeps answering; a later request for the worktree starts a fresh router.
/// </summary>
public class WorktreeReleaseTests
{
    [Fact]
    public async Task Release_closes_the_worktree_and_the_primary_keeps_answering()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var repo = FixtureRepo.CreateStandard();
        var log = new EngineLog(repo.Root.StateDirectory);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scheduler = new Scheduler(Scheduler.DefaultReaderCount);
        var router = new RequestRouter(repo.Root, log, scheduler, new MemoryBudget());
        var wt2Dir = Path.Combine(Path.GetTempPath(), "fuse-wt-release-" + Guid.NewGuid().ToString("N"));
        string? wt2State = null;
        try
        {
            await router.InitializeAsync(shutdown.Token);
            Assert.Empty((await CheckAsync(router, worktreeRoot: null)).Report.Errors);

            RunGit(repo.Root.Path, "worktree", "add", "--detach", wt2Dir);
            var wt2 = RepoRoot.Find(wt2Dir);
            Assert.NotNull(wt2);
            wt2State = wt2.StateDirectory;
            Assert.Empty((await CheckAsync(router, wt2.Path)).Report.Errors);

            Assert.True(router.TryReleaseWorktree(wt2.Path));
            Assert.False(router.TryReleaseWorktree(repo.Root.Path));
            Assert.False(router.TryReleaseWorktree(null));
            Assert.False(router.TryReleaseWorktree(""));

            // A later request for the released worktree starts a fresh router for it.
            Assert.Empty((await CheckAsync(router, wt2.Path)).Report.Errors);
            Assert.True(router.TryReleaseWorktree(wt2.Path));

            await DeleteWithRetriesAsync(wt2Dir, cancellationToken);
            Assert.False(Directory.Exists(wt2Dir));

            Assert.Empty((await CheckAsync(router, worktreeRoot: null)).Report.Errors);
        }
        finally
        {
            PruneWorktree(repo.Root.Path, wt2Dir);
            await shutdown.CancelAsync();
            await router.WaitForPreloadAsync();
            router.Dispose();
            if (wt2State is not null)
                DeleteQuietly(wt2State);
        }
    }

    private static async Task<EngineResponse.CheckAnswered> CheckAsync(RequestRouter router, string? worktreeRoot) =>
        Assert.IsType<EngineResponse.CheckAnswered>(await router.HandleAsync(
            new EngineRequest.CheckChanges(WaitForLoad: true) { BuildId = EngineVersion.Build, WorktreeRoot = worktreeRoot },
            TestContext.Current.CancellationToken));

    /// <summary>
    ///     Deletes <paramref name="directory"/>, retrying while the worktree's just-killed git reader still holds it.
    /// </summary>
    private static async Task DeleteWithRetriesAsync(string directory, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 50)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
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

    /// <summary>Drops the worktree's registration and whatever is left of its directory, best effort.</summary>
    private static void PruneWorktree(string repoPath, string worktreeDir)
    {
        try
        {
            if (Directory.Exists(worktreeDir))
                Directory.Delete(worktreeDir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        try
        {
            RunGit(repoPath, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void DeleteQuietly(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
