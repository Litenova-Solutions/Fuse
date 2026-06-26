using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// R9: the deterministic core of the task-resolution harness - apply a patch in an isolated worktree and run a
// test oracle. A trivial git fixture proves the oracle: a known-good patch passes, a known-bad patch fails,
// and a failing task does not corrupt the next.
public sealed class TaskResolutionHarnessTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // The oracle: "git grep FIXED" succeeds (exit 0) only when the marker is present in a tracked file.
    private static readonly OracleCommand MarkerOracle = new("git", ["grep", "-q", "FIXED"]);

    [Fact]
    public async Task KnownGoodPatch_applies_and_passes_the_oracle()
    {
        var repo = await NewRepoAsync();
        try
        {
            var head = (await GitCli.RunAsync(repo, Ct, "rev-parse", "HEAD")).StdOut.Trim();
            var harness = new TaskResolutionHarness(new CorpusManager(NewTempDir(), NewTempDir()));

            var result = await harness.ResolveAsync(repo, head, GoodPatch, MarkerOracle, Ct);

            Assert.True(result.PatchApplied, result.Detail);
            Assert.True(result.TestsPassed, result.Detail);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task KnownBadPatch_applies_but_fails_the_oracle()
    {
        var repo = await NewRepoAsync();
        try
        {
            var head = (await GitCli.RunAsync(repo, Ct, "rev-parse", "HEAD")).StdOut.Trim();
            var harness = new TaskResolutionHarness(new CorpusManager(NewTempDir(), NewTempDir()));

            var result = await harness.ResolveAsync(repo, head, BadPatch, MarkerOracle, Ct);

            Assert.True(result.PatchApplied, result.Detail);
            Assert.False(result.TestsPassed);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    [Fact]
    public async Task FailingTask_does_not_corrupt_the_next_task()
    {
        var repo = await NewRepoAsync();
        try
        {
            var head = (await GitCli.RunAsync(repo, Ct, "rev-parse", "HEAD")).StdOut.Trim();
            var harness = new TaskResolutionHarness(new CorpusManager(NewTempDir(), NewTempDir()));

            // A bad patch first, then a good one against the same base: isolation means the second still passes.
            var bad = await harness.ResolveAsync(repo, head, BadPatch, MarkerOracle, Ct);
            var good = await harness.ResolveAsync(repo, head, GoodPatch, MarkerOracle, Ct);

            Assert.False(bad.TestsPassed);
            Assert.True(good.PatchApplied, good.Detail);
            Assert.True(good.TestsPassed, good.Detail);
        }
        finally
        {
            TryDelete(repo);
        }
    }

    // The seed file says "TODO"; the good patch replaces it with "FIXED", the bad patch with "STILL_TODO".
    private const string GoodPatch =
        "--- a/work.txt\n+++ b/work.txt\n@@ -1 +1 @@\n-TODO\n+FIXED\n";

    private const string BadPatch =
        "--- a/work.txt\n+++ b/work.txt\n@@ -1 +1 @@\n-TODO\n+STILL_TODO\n";

    private static async Task<string> NewRepoAsync()
    {
        var repo = NewTempDir();
        await GitCli.RunAsync(repo, Ct, "init", "-q", "-b", "main");
        await GitCli.RunAsync(repo, Ct, "config", "user.email", "t@t.test");
        await GitCli.RunAsync(repo, Ct, "config", "user.name", "Tester");
        await GitCli.RunAsync(repo, Ct, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "work.txt"), "TODO\n");
        await GitCli.RunAsync(repo, Ct, "add", "-A");
        await GitCli.RunAsync(repo, Ct, "commit", "-q", "-m", "seed");
        return repo;
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-taskres-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
