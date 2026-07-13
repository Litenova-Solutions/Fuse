using Fuse.Indexing;
using Fuse.Workspace;
using Xunit;

namespace Fuse.Workspace.Tests;

// F2 candidate racing: the adjudication and concurrency contract of CandidateRacer, driven by a fake check
// delegate so these run without an SDK/binlog. A companion binlog-backed test (CandidateRaceResidentTests)
// proves fork-sharing wall-clock and verdict equality against a real held compilation.
public sealed class CandidateRacerTests
{
    private static RaceCandidate C(string id, string file = "A.cs") => new(id, file, $"// {id}");

    private static IReadOnlyList<CheckDiagnostic> Error(string id) =>
        [new CheckDiagnostic("CS0103", "Error", $"broken {id}", "A.cs", 1)];

    private static IReadOnlyList<CheckDiagnostic> Clean() => [];

    [Fact]
    public async Task A_single_clean_candidate_strictly_dominates_the_red_ones()
    {
        var report = await CandidateRacer.RaceAsync(
            (c, _) => Task.FromResult<IReadOnlyList<CheckDiagnostic>?>(c.Id == "a" ? Clean() : Error(c.Id)),
            [C("a"), C("b"), C("c")], CancellationToken.None);

        Assert.Equal("a", report.WinnerId);
        Assert.False(report.Tie);
        Assert.Single(report.Clean);
        Assert.True(report.Verdicts.Single(v => v.Id == "a").IsClean);
        Assert.False(report.Verdicts.Single(v => v.Id == "b").IsClean);
    }

    [Fact]
    public async Task Two_clean_candidates_are_reported_as_a_tie_with_no_winner()
    {
        var report = await CandidateRacer.RaceAsync(
            (c, _) => Task.FromResult<IReadOnlyList<CheckDiagnostic>?>(c.Id == "c" ? Error(c.Id) : Clean()),
            [C("a"), C("b"), C("c")], CancellationToken.None);

        // Strict dominance cannot choose one green over another green: two clean candidates are a tie.
        Assert.Null(report.WinnerId);
        Assert.True(report.Tie);
        Assert.Equal(2, report.Clean.Count);
    }

    [Fact]
    public async Task No_clean_candidate_yields_no_winner_and_no_tie()
    {
        var report = await CandidateRacer.RaceAsync(
            (c, _) => Task.FromResult<IReadOnlyList<CheckDiagnostic>?>(Error(c.Id)),
            [C("a"), C("b")], CancellationToken.None);

        Assert.Null(report.WinnerId);
        Assert.False(report.Tie);
        Assert.Empty(report.Clean);
    }

    [Fact]
    public async Task A_not_applicable_candidate_is_neither_clean_nor_red()
    {
        // A null result (no held compilation covers the file) is not-applicable: it does not count as clean, so a
        // lone truly-clean candidate still wins, and the not-applicable one is flagged distinctly.
        var report = await CandidateRacer.RaceAsync(
            (c, _) => Task.FromResult<IReadOnlyList<CheckDiagnostic>?>(c.Id == "a" ? Clean() : c.Id == "b" ? null : Error(c.Id)),
            [C("a"), C("b"), C("c")], CancellationToken.None);

        Assert.Equal("a", report.WinnerId);
        var notApplicable = report.Verdicts.Single(v => v.Id == "b");
        Assert.False(notApplicable.Applicable);
        Assert.False(notApplicable.IsClean);
    }

    [Fact]
    public async Task Race_verdicts_equal_running_each_candidate_alone()
    {
        // Verdict equality (the F2 gate): a raced candidate's diagnostics equal what it gets checked alone. The
        // fake check is deterministic per candidate, so the raced verdict must carry that exact diagnostic set.
        IReadOnlyList<CheckDiagnostic>? Check(RaceCandidate c) =>
            c.Id == "b" ? [new CheckDiagnostic("CS1061", "Error", "no member", "A.cs", 7)] : Clean();

        var candidates = new[] { C("a"), C("b"), C("c") };
        var report = await CandidateRacer.RaceAsync((c, _) => Task.FromResult(Check(c)), candidates, CancellationToken.None);

        foreach (var candidate in candidates)
        {
            var alone = Check(candidate) ?? [];
            var raced = report.Verdicts.Single(v => v.Id == candidate.Id).Diagnostics;
            Assert.Equal(alone.Select(d => (d.Id, d.Line)), raced.Select(d => (d.Id, d.Line)));
        }

        // Order is preserved: the report lists candidates in input order.
        Assert.Equal(new[] { "a", "b", "c" }, report.Verdicts.Select(v => v.Id));
    }

    [Fact]
    public async Task Candidates_are_evaluated_one_at_a_time_without_cross_contamination()
    {
        // F2's Fallback ships sequential evaluation (concurrent Roslyn binding over shared-base forks serializes,
        // so parallelism buys no wall-clock and only multiplies live-fork memory). This proves it: at most one
        // check runs at a time (peak concurrency 1, the memory bound), and each verdict carries its own
        // candidate's diagnostics (id echoed into the message), so forks are isolated, not shared state.
        var concurrent = 0;
        var peak = 0;
        var lockObj = new object();

        async Task<IReadOnlyList<CheckDiagnostic>?> Check(RaceCandidate c, CancellationToken ct)
        {
            lock (lockObj) { concurrent++; peak = Math.Max(peak, concurrent); }
            await Task.Delay(15, ct);
            lock (lockObj) { concurrent--; }
            return [new CheckDiagnostic("CSX", "Error", c.Id, "A.cs", 1)];
        }

        var candidates = Enumerable.Range(0, 6).Select(i => C($"k{i}")).ToList();
        var report = await CandidateRacer.RaceAsync(Check, candidates, CancellationToken.None);

        Assert.Equal(1, peak); // one fork live at a time - the kill-risk (memory under k forks) mitigation
        foreach (var candidate in candidates)
            Assert.Equal(candidate.Id, report.Verdicts.Single(v => v.Id == candidate.Id).Diagnostics[0].Message);
    }

    [Fact]
    public async Task Empty_candidate_list_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => CandidateRacer.RaceAsync(
            (_, _) => Task.FromResult<IReadOnlyList<CheckDiagnostic>?>(Clean()),
            [], CancellationToken.None));
    }
}
