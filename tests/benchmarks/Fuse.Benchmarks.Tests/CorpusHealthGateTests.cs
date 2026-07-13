using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// C4: the model-suite refusal gate. A model-driven suite may run only behind a corpus-health.json that exists,
// is newer than the corpus manifest, and meets the minimums. These pin every refusal path.
public sealed class CorpusHealthGateTests
{
    private static CorpusHealthReport Report(int reposTier1, int tasksVerified, string generated) => new(
        Generated: generated,
        ReposTotal: reposTier1,
        ReposTier1: reposTier1,
        TasksTotal: tasksVerified,
        TasksVerified: tasksVerified,
        MinReposTier1: CorpusHealthReport.GateMinReposTier1,
        MinTasksVerified: CorpusHealthReport.GateMinTasksVerified,
        Repos: [],
        Notes: []);

    private static readonly DateTime Manifest = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void No_report_is_refused()
    {
        var d = CorpusHealthGate.Evaluate(report: null, reportGeneratedUtc: null, manifestModifiedUtc: Manifest);
        Assert.False(d.Allowed);
        Assert.Contains("corpus-health", d.Reason);
    }

    [Fact]
    public void A_report_older_than_the_manifest_is_refused_as_stale()
    {
        var stale = Manifest.AddDays(-1);
        var d = CorpusHealthGate.Evaluate(Report(20, 60, stale.ToString("O")), stale, Manifest);
        Assert.False(d.Allowed);
        Assert.Contains("older than the corpus manifest", d.Reason);
    }

    [Fact]
    public void A_fresh_report_below_the_reduced_scope_floor_is_refused()
    {
        var fresh = Manifest.AddDays(1);
        var d = CorpusHealthGate.Evaluate(Report(5, 0, fresh.ToString("O")), fresh, Manifest);
        Assert.False(d.Allowed);
        Assert.False(d.ReducedScope);
        Assert.Contains("below the reduced-scope floor", d.Reason);
    }

    [Fact]
    public void A_fresh_report_meeting_minimums_is_allowed_at_full_scope()
    {
        var fresh = Manifest.AddDays(1);
        var d = CorpusHealthGate.Evaluate(Report(20, 60, fresh.ToString("O")), fresh, Manifest);
        Assert.True(d.Allowed, d.Reason);
        Assert.False(d.ReducedScope);
    }

    [Fact]
    public void Below_minimums_but_at_the_reduced_scope_floor_is_allowed_reduced_scope()
    {
        // The C4/D20 fallback: tier below 20 and tasks below 60, but at least the reduced-scope task floor (40),
        // so a no-headline pilot runs rather than refusing outright (matches the recorded 15 tier-1, 44 tasks).
        var fresh = Manifest.AddDays(1);
        var d = CorpusHealthGate.Evaluate(Report(15, 44, fresh.ToString("O")), fresh, Manifest);
        Assert.True(d.Allowed, d.Reason);
        Assert.True(d.ReducedScope);
        Assert.Contains("REDUCED-SCOPE", d.Reason);
    }

    [Fact]
    public void Just_below_the_reduced_scope_floor_is_refused()
    {
        var fresh = Manifest.AddDays(1);
        var d = CorpusHealthGate.Evaluate(Report(15, CorpusHealthReport.ReducedScopeTaskFloor - 1, fresh.ToString("O")), fresh, Manifest);
        Assert.False(d.Allowed);
    }
}
