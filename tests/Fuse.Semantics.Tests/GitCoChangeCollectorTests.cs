using System.Linq;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// A6: the git co-change collector aggregates file pairs that change together, computes PMI and Jaccard, skips
// wide commits and non-source files, and drops one-off pairs. The aggregation is tested over synthetic git-log
// text so it is deterministic and does not require a git executable.
public sealed class GitCoChangeCollectorTests
{
    private static string Log(params string[] commits) => string.Join('\n', commits);

    private static string Commit(string date, params string[] files) =>
        "\x01" + date + "\n" + string.Join('\n', files);

    [Fact]
    public void AggregatesPairsAndDropsOneOffCouplings()
    {
        var collector = new GitCoChangeCollector();
        var log = Log(
            Commit("2024-03-01T00:00:00Z", "src/A.cs", "src/B.cs"),
            Commit("2024-02-01T00:00:00Z", "src/A.cs", "src/B.cs", "src/C.cs"),
            Commit("2024-01-01T00:00:00Z", "src/A.cs"));

        var records = collector.ParseLog(log);

        // (A,B) co-change twice and is kept; (A,C) and (B,C) co-change once and fall below the floor.
        var ab = Assert.Single(records);
        Assert.Equal("src/A.cs", ab.PathA);
        Assert.Equal("src/B.cs", ab.PathB);
        Assert.Equal(2, ab.Count);
        // Jaccard = count / (countA + countB - count) = 2 / (3 + 2 - 2) = 2/3.
        Assert.Equal(2.0 / 3.0, ab.Jaccard, 5);
        // Most recent shared commit date is carried (git log is newest-first).
        Assert.Equal("2024-03-01T00:00:00Z", ab.LastSeenUtc);
    }

    [Fact]
    public void IgnoresNonSourceFiles()
    {
        var collector = new GitCoChangeCollector();
        var log = Log(
            Commit("2024-03-01T00:00:00Z", "src/A.cs", "README.md", "build.lock"),
            Commit("2024-02-01T00:00:00Z", "src/A.cs", "README.md", "build.lock"));

        var records = collector.ParseLog(log);

        // Only A.cs is a source file; with no source-file pair, nothing is emitted (the docs/lock churn is ignored).
        Assert.Empty(records);
    }

    [Fact]
    public void SkipsWideCommits()
    {
        var collector = new GitCoChangeCollector();
        // A sweep commit touching more than the per-commit cap is skipped, so it contributes no pairs; a later
        // pair of normal commits over two files is still counted.
        var wideFiles = Enumerable.Range(0, GitCoChangeCollector.MaxFilesPerCommit + 5).Select(i => $"src/F{i}.cs").ToArray();
        var log = Log(
            Commit("2024-03-01T00:00:00Z", wideFiles),
            Commit("2024-02-01T00:00:00Z", "src/X.cs", "src/Y.cs"),
            Commit("2024-01-01T00:00:00Z", "src/X.cs", "src/Y.cs"));

        var records = collector.ParseLog(log);

        var xy = Assert.Single(records);
        Assert.Equal("src/X.cs", xy.PathA);
        Assert.Equal("src/Y.cs", xy.PathB);
        Assert.Equal(2, xy.Count);
    }
}
