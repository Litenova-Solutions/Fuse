using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

public sealed class CorpusManagerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task ReconstructPullRequests_recovers_a_merge_commit_change_set()
    {
        var repo = NewTempDir();
        try
        {
            await Git(repo, "init", "-q", "-b", "main");
            await Git(repo, "config", "user.email", "t@t.test");
            await Git(repo, "config", "user.name", "Tester");
            await Git(repo, "config", "commit.gpgsign", "false");

            await WriteAndCommit(repo, "a.cs", "class A {}", "Initial commit");
            await Git(repo, "checkout", "-q", "-b", "feature");
            File.WriteAllText(Path.Combine(repo, "a.cs"), "class A { void M() {} }");
            File.WriteAllText(Path.Combine(repo, "b.cs"), "class B {}");
            await Git(repo, "add", "-A");
            await Git(repo, "commit", "-q", "-m", "Add method M and class B");
            await Git(repo, "checkout", "-q", "main");
            await Git(repo, "merge", "--no-ff", "-m", "Merge pull request #7 from acme/feature", "feature");

            var manager = new CorpusManager(NewTempDir(), NewTempDir());
            var prs = await manager.ReconstructPullRequestsAsync(repo, "Demo", 10, Ct);

            var pr = Assert.Single(prs);
            Assert.Equal(7, pr.Pr);
            Assert.Equal("Demo", pr.Repo);
            Assert.Equal("Add method M and class B", pr.Title);
            Assert.Contains("a.cs", pr.ChangedCs);
            Assert.Contains("b.cs", pr.ChangedCs);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task AddWorktree_then_remove_materializes_the_tree()
    {
        var repo = NewTempDir();
        try
        {
            await Git(repo, "init", "-q", "-b", "main");
            await Git(repo, "config", "user.email", "t@t.test");
            await Git(repo, "config", "user.name", "Tester");
            await Git(repo, "config", "commit.gpgsign", "false");
            await WriteAndCommit(repo, "a.cs", "class A {}", "Initial commit");
            var head = (await GitCli.RunAsync(repo, Ct, "rev-parse", "HEAD")).StdOut.Trim();

            var manager = new CorpusManager(NewTempDir(), NewTempDir());
            var worktree = await manager.AddWorktreeAsync(repo, head, Ct);

            Assert.NotNull(worktree);
            Assert.True(File.Exists(Path.Combine(worktree!, "a.cs")));

            await manager.RemoveWorktreeAsync(repo, worktree!, Ct);
            Assert.False(Directory.Exists(worktree!));
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public void LoadManifest_parses_the_pinned_corpus()
    {
        var benchRoot = BenchRoot();
        if (benchRoot is null)
            return; // bench root not located (out-of-tree run); skip rather than fail.

        var manager = new CorpusManager(benchRoot, Path.Combine(benchRoot, ".corpus"));
        var manifest = manager.LoadManifest();

        Assert.Equal("o200k_base", manifest.Tokenizer);
        Assert.Contains(manifest.Repos, r => r.Name == "MediatR" && r.Commit is { Length: > 0 });
    }

    [Fact]
    public void LoadDataset_lifts_prs_into_tasks_with_signal_buckets()
    {
        var benchRoot = BenchRoot();
        if (benchRoot is null)
            return;

        var manager = new CorpusManager(benchRoot, Path.Combine(benchRoot, ".corpus"));
        var dataset = manager.LoadDataset("dotnet-prs-v1");

        var allTasks = dataset.Repos.SelectMany(r => r.Tasks).ToList();
        Assert.Equal(108, allTasks.Count);
        Assert.All(allTasks, t =>
        {
            Assert.NotEmpty(t.GroundTruth.Files);
            Assert.False(string.IsNullOrEmpty(t.Category));
        });
        // The six corpus repositories are all represented.
        Assert.Equal(6, dataset.Repos.Count);
    }

    private static async Task WriteAndCommit(string repo, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, file), content);
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-q", "-m", message);
    }

    private static async Task Git(string repo, params string[] args)
    {
        var result = await GitCli.RunAsync(repo, Ct, args);
        if (!result.Ok)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.StdErr}");
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-cm-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        try
        {
            // Git pack/object files are marked read-only on Windows; clear the attribute before deleting.
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? BenchRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Fuse.slnx")))
                return Path.Combine(dir, "tests", "benchmarks");
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
