using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// C4: the corpus-health gate math. A model-driven suite may run only when the corpus meets BOTH minimums -
// at least 20 tier-1 repositories and at least 60 verified oracle tasks. These tests pin the boundary so a
// future edit cannot silently loosen the gate.
public sealed class CorpusHealthReportTests
{
    private static CorpusHealthReport Report(int reposTier1, int tasksVerified) => new(
        Generated: "2026-07-09T00:00:00Z",
        ReposTotal: reposTier1,
        ReposTier1: reposTier1,
        TasksTotal: tasksVerified,
        TasksVerified: tasksVerified,
        MinReposTier1: CorpusHealthReport.GateMinReposTier1,
        MinTasksVerified: CorpusHealthReport.GateMinTasksVerified,
        Repos: [],
        Notes: []);

    [Fact]
    public void Meets_minimums_only_at_or_above_both_thresholds()
    {
        Assert.True(Report(20, 60).MeetsMinimums);
        Assert.True(Report(25, 80).MeetsMinimums);
    }

    [Theory]
    [InlineData(19, 60)] // one repo short
    [InlineData(20, 59)] // one task short
    [InlineData(0, 0)]
    [InlineData(19, 59)]
    public void Below_either_threshold_does_not_meet_minimums(int reposTier1, int tasksVerified)
        => Assert.False(Report(reposTier1, tasksVerified).MeetsMinimums);

    [Fact]
    public void Gate_minimums_are_20_repos_and_60_tasks()
    {
        Assert.Equal(20, CorpusHealthReport.GateMinReposTier1);
        Assert.Equal(60, CorpusHealthReport.GateMinTasksVerified);
    }
}
