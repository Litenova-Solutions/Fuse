using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// R4 (harness-first): the loop-metric computation is deterministic and model-free, so it is unit-tested against
// scripted transcripts and carries the claim between the expensive model-driven runs.
public sealed class LoopMetricsTests
{
    [Fact]
    public void Counts_build_invocations_and_iterations_to_green()
    {
        // edit, build(fail), edit, build(fail), edit, build(pass): three build-gated turns to reach green.
        var turns = new[]
        {
            new TranscriptTurn(TurnKind.Edit, false, 100),
            new TranscriptTurn(TurnKind.Build, false, 20_000),
            new TranscriptTurn(TurnKind.Edit, false, 100),
            new TranscriptTurn(TurnKind.Build, false, 20_000),
            new TranscriptTurn(TurnKind.Edit, false, 100),
            new TranscriptTurn(TurnKind.Build, true, 20_000),
        };

        var m = LoopMetrics.Compute(turns);

        Assert.True(m.ReachedGreen);
        Assert.Equal(3, m.IterationsToGreen);
        Assert.Equal(3, m.BuildInvocations);
        Assert.Equal(60_300, m.WallClockMs);
    }

    [Fact]
    public void A_test_turn_counts_toward_green_but_not_build_invocations()
    {
        var turns = new[]
        {
            new TranscriptTurn(TurnKind.Build, false, 10_000),
            new TranscriptTurn(TurnKind.Edit, false, 50),
            new TranscriptTurn(TurnKind.Build, true, 10_000),
            new TranscriptTurn(TurnKind.Test, true, 5_000),
        };

        var m = LoopMetrics.Compute(turns);

        Assert.True(m.ReachedGreen);
        // First passing gated turn is the second build (gated turns seen: build, build -> 2).
        Assert.Equal(2, m.IterationsToGreen);
        Assert.Equal(2, m.BuildInvocations); // two Build turns; the Test turn is not a build invocation
        Assert.Equal(1, m.TestInvocations);
        Assert.Equal(3, m.AgentVisibleVerifications); // 2 builds + 1 test
    }

    [Fact]
    public void A_fuse_check_is_counted_in_its_own_column_not_the_build_column()
    {
        // D22a: a session that verifies only with fuse_check reaches green (the proxy) but records zero
        // agent-visible build round-trips - the whole point of separating the columns.
        var turns = new[]
        {
            new TranscriptTurn(TurnKind.Check, false, 30),
            new TranscriptTurn(TurnKind.Edit, false, 50),
            new TranscriptTurn(TurnKind.Check, true, 30),
        };

        var m = LoopMetrics.Compute(turns);

        Assert.True(m.ReachedGreen);
        Assert.Equal(2, m.IterationsToGreen); // two check turns, green on the second
        Assert.Equal(0, m.BuildInvocations);
        Assert.Equal(2, m.CheckInvocations);
        Assert.Equal(0, m.AgentVisibleVerifications);
    }

    [Fact]
    public void Never_green_reports_zero_iterations_and_all_builds()
    {
        var turns = new[]
        {
            new TranscriptTurn(TurnKind.Build, false, 10_000),
            new TranscriptTurn(TurnKind.Build, false, 10_000),
        };

        var m = LoopMetrics.Compute(turns);

        Assert.False(m.ReachedGreen);
        Assert.Equal(0, m.IterationsToGreen);
        Assert.Equal(2, m.BuildInvocations);
    }

    [Fact]
    public void Uses_measured_end_to_end_duration_when_transcript_turns_have_no_timing()
    {
        var turns = new[]
        {
            new TranscriptTurn(TurnKind.Edit, false, 0),
            new TranscriptTurn(TurnKind.Build, true, 0),
        };

        var metrics = LoopMetrics.Compute(turns, wallClockMs: 12_345);

        Assert.True(metrics.ReachedGreen);
        Assert.Equal(12_345, metrics.WallClockMs);
    }

    [Fact]
    public void Summarizes_elapsed_time_by_arm_and_clusters_repeated_rollouts_by_task()
    {
        var results = new[]
        {
            Result("task-a/native#1", "native", 100),
            Result("task-a/native#2", "native", 300),
            Result("task-b/native#1", "native", 500),
            Result("task-a/fuse#1", "fuse", 200),
            Result("task-b/fuse#1", "fuse", 700),
            Result("task-c/fuse#1", "fuse", 0), // Legacy checkpoint latency is unusable and excluded.
        };

        var summaries = LoopTimingMetrics.SummarizeArms(results);

        var native = Assert.Single(summaries, summary => summary.Arm == "native");
        Assert.Equal(3, native.RolloutCount);
        Assert.Equal(2, native.TaskCount);
        Assert.Equal(900, native.TotalMs);
        Assert.Equal(300, native.MeanRolloutMs);
        Assert.Equal(300, native.MedianRolloutMs);
        Assert.Equal(350, native.MeanTaskMs); // Mean of task-a's 200 and task-b's 500.
        Assert.Equal(350, native.MedianTaskMs);

        var fuse = Assert.Single(summaries, summary => summary.Arm == "fuse");
        Assert.Equal(2, fuse.RolloutCount);
        Assert.Equal(2, fuse.TaskCount);
        Assert.Equal(450, fuse.MeanTaskMs);
    }

    [Fact]
    public void Paired_elapsed_comparison_uses_only_tasks_present_in_both_arms()
    {
        var results = new[]
        {
            Result("task-a/native#1", "native", 100),
            Result("task-a/native#2", "native", 300),
            Result("task-a/fuse#1", "fuse", 200),
            Result("task-b/native#1", "native", 500),
            Result("task-b/fuse#1", "fuse", 700),
            Result("task-c/fuse#1", "fuse", 50), // Unpaired, so it cannot bias the comparison.
        };

        var paired = LoopTimingMetrics.ComparePaired(results, "native", "fuse");

        Assert.Equal(2, paired.PairedTaskCount);
        Assert.Equal(100, paired.MeanDeltaMs); // Per-task deltas are 0 and 200.
        Assert.Equal(100, paired.MedianDeltaMs);
    }

    [Fact]
    public void Verified_elapsed_comparison_excludes_fast_failures()
    {
        var results = new[]
        {
            Result("task-a/native#1", "native", 1_000, oraclePassed: true),
            Result("task-a/fuse#1", "fuse", 500, oraclePassed: true),
            Result("task-b/native#1", "native", 2_000, oraclePassed: true),
            Result("task-b/fuse#1", "fuse", 100, oraclePassed: false),
            Result("task-c/native#1", "native", 2_000, oraclePassed: null),
            Result("task-c/fuse#1", "fuse", 500, oraclePassed: true),
        };

        var paired = LoopTimingMetrics.ComparePairedVerified(results, "native", "fuse");

        Assert.Equal(1, paired.VerifiedPairCount);
        Assert.Equal(-500, paired.MeanDeltaMs);
        Assert.Equal(-500, paired.MedianDeltaMs);
        Assert.Equal(0.5, paired.MedianRelativeSavings);
    }

    private static TaskResult Result(string id, string arm, long latencyMs, bool? oraclePassed = null) =>
        new(id, "repo", arm, 0, 0, 0, latencyMs, new TaskFiles([], [], []), OraclePassed: oraclePassed);
}
