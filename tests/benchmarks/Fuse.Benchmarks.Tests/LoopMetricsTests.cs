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
}
