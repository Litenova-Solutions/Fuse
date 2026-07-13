using System.Text.Json;
using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// B1: the pure task-file load the loop suite depends on - reading the persisted corpus-v2 verified oracle set,
// mapping it to loop tasks (resolving repo paths, dropping absent repos, capping per repo), and the
// dotnet-prs-v1 fallback mapping. The claude-driven rollout is exercised only by a provisioned run; these pin
// the deterministic selection so a replay always sees the same task list.
public sealed class LoopTaskSourceTests
{
    [Fact]
    public void ReadCorpusTaskFile_returns_null_when_absent()
    {
        var dir = NewTempDir();
        try
        {
            Assert.Null(LoopTaskSource.ReadCorpusTaskFile(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadCorpusTaskFile_returns_null_when_task_list_empty()
    {
        var dir = NewTempDir();
        try
        {
            WriteSet(dir, new CorpusTaskSet("2026-07-10T00:00:00Z", []));
            Assert.Null(LoopTaskSource.ReadCorpusTaskFile(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ReadCorpusTaskFile_reads_a_written_set()
    {
        var dir = NewTempDir();
        try
        {
            var set = new CorpusTaskSet("2026-07-10T00:00:00Z",
            [
                new CorpusTaskRecord("Scrutor", "aaaaaaaabbbb", "ccccccccdddd", "FullyQualifiedName~T", "Add a thing", ["tests/TTests.cs"]),
            ]);
            WriteSet(dir, set);

            var loaded = LoopTaskSource.ReadCorpusTaskFile(dir);

            Assert.NotNull(loaded);
            var task = Assert.Single(loaded!.Tasks);
            Assert.Equal("Scrutor", task.Repo);
            Assert.Equal("aaaaaaaabbbb", task.BaseCommit);
            Assert.Equal("ccccccccdddd", task.MergeCommit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FromCorpusTasks_maps_records_and_resolves_paths()
    {
        var set = new CorpusTaskSet("g",
        [
            new CorpusTaskRecord("Scrutor", "0123456789ab", "ffffffffffff", "FullyQualifiedName~ScanTests", "Add scanning", ["tests/ScanTests.cs"]),
        ]);

        var tasks = LoopTaskSource.FromCorpusTasks(set, _ => "/repos/Scrutor", repoFilter: null, perRepoCap: 0);

        var task = Assert.Single(tasks);
        Assert.Equal("Scrutor@01234567", task.Id); // repo@short-sha (8 chars)
        Assert.Equal("Scrutor", task.Repo);
        Assert.Equal("/repos/Scrutor", task.RepoPath);
        Assert.Equal("0123456789ab", task.BaseRef);
        Assert.Equal("Add scanning", task.Title);
        // D22a: the oracle fields carry through so the loop suite can run the fail-to-pass post-check.
        Assert.Equal("ffffffffffff", task.MergeCommit);
        Assert.Equal("FullyQualifiedName~ScanTests", task.TestFilter);
        Assert.Equal(["tests/ScanTests.cs"], task.TestFiles);
        Assert.True(task.HasOracle);
    }

    [Fact]
    public void FromCorpusTasks_task_without_gold_tests_has_no_oracle()
    {
        var set = new CorpusTaskSet("g",
        [
            new CorpusTaskRecord("Scrutor", "0123456789ab", "ffffffffffff", "FullyQualifiedName~X", "T", []),
        ]);

        var task = Assert.Single(LoopTaskSource.FromCorpusTasks(set, _ => "/x", null, 0));
        Assert.False(task.HasOracle); // no gold test files -> the oracle post-check is skipped, not guessed
    }

    [Fact]
    public void FromCorpusTasks_drops_repos_that_do_not_resolve()
    {
        var set = new CorpusTaskSet("g",
        [
            new CorpusTaskRecord("Present", "aaaaaaaa1111", "m", "f", "t1", []),
            new CorpusTaskRecord("Absent", "bbbbbbbb2222", "m", "f", "t2", []),
        ]);

        var tasks = LoopTaskSource.FromCorpusTasks(
            set, name => name == "Present" ? "/repos/Present" : null, repoFilter: null, perRepoCap: 0);

        var task = Assert.Single(tasks);
        Assert.Equal("Present", task.Repo);
    }

    [Fact]
    public void FromCorpusTasks_applies_per_repo_cap_and_repo_filter()
    {
        var set = new CorpusTaskSet("g",
        [
            new CorpusTaskRecord("A", "a1a1a1a1a1a1", "m", "f", "t", []),
            new CorpusTaskRecord("A", "a2a2a2a2a2a2", "m", "f", "t", []),
            new CorpusTaskRecord("A", "a3a3a3a3a3a3", "m", "f", "t", []),
            new CorpusTaskRecord("B", "b1b1b1b1b1b1", "m", "f", "t", []),
        ]);

        var capped = LoopTaskSource.FromCorpusTasks(set, _ => "/x", repoFilter: null, perRepoCap: 2);
        Assert.Equal(3, capped.Count); // 2 from A (capped) + 1 from B

        var filtered = LoopTaskSource.FromCorpusTasks(set, _ => "/x", repoFilter: "A", perRepoCap: 0);
        Assert.Equal(3, filtered.Count); // all of A, none of B
        Assert.All(filtered, t => Assert.Equal("A", t.Repo));
    }

    [Fact]
    public void FromDataset_maps_eligible_tasks_and_drops_ineligible()
    {
        var repo = new RepoTasks("Repo", "Repo", "/repos/Repo",
        [
            Task("Repo#1", "base1", "head1", "Add ParseThing method", hasFiles: true),      // eligible
            Task("Repo#2", "base2", "head2", "Merge pull request #9", hasFiles: true),       // low-signal (no-signal)
            Task("Repo#3", null, "head3", "Add another method", hasFiles: true),             // missing base ref
            Task("Repo#4", "base4", "head4", "Add yet another", hasFiles: false),            // no ground-truth files
        ]);
        var dataset = new EvalDataset("dotnet-prs-v1", [repo]);

        var tasks = LoopTaskSource.FromDataset(dataset, repoFilter: null, perRepo: 10);

        var task = Assert.Single(tasks);
        Assert.Equal("Repo#1", task.Id);
        Assert.Equal("base1", task.BaseRef);
    }

    [Fact]
    public void FromDataset_skips_repos_without_a_resolved_path()
    {
        var repo = new RepoTasks("Repo", "Repo", Path: null,
        [
            Task("Repo#1", "base1", "head1", "Add ParseThing method", hasFiles: true),
        ]);
        var dataset = new EvalDataset("dotnet-prs-v1", [repo]);

        Assert.Empty(LoopTaskSource.FromDataset(dataset, repoFilter: null, perRepo: 10));
    }

    private static PrTask Task(string id, string? baseRef, string? headRef, string title, bool hasFiles)
    {
        var files = hasFiles ? new[] { new GroundTruthFile("src/Thing.cs", "changed") } : [];
        return new PrTask(
            id, "pull_request", "Repo", Pr: 1, baseRef, headRef, MergeRef: "merge", title, Body: null,
            SignalBucket.Classify(title),
            new GroundTruth(files, [], [], []));
    }

    private static void WriteSet(string dir, CorpusTaskSet set)
        => File.WriteAllText(
            Path.Combine(dir, CorpusTaskSet.FileName),
            JsonSerializer.Serialize(set, BenchmarkJsonContext.Default.CorpusTaskSet));

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fuse-loop-task-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
