using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// Suite F (R1): the deterministic in-process core must classify every known-good and known-bad edit correctly,
// so the false-green and false-red counts are both zero and the gate passes. This is the honesty gate for
// fuse_check exercised without a build-capture worker (which the offline test run does not provision).
public sealed class CheckGateSuiteTests
{
    [Fact]
    public async Task Gate_passes_with_no_false_green_and_no_false_red()
    {
        var suite = new CheckGateSuite();
        var options = new EvalOptions(BenchRoot: Path.GetTempPath());

        var result = await suite.RunAsync(options, CancellationToken.None);

        Assert.Equal("checkgate", result.Suite);
        Assert.NotEmpty(result.Tasks);
        // Every case is classified correctly, so recall (accuracy) is 1.0 and the gate note reports PASS.
        Assert.Equal(1.0, result.Scorecard.Recall, 3);
        Assert.Contains(result.Notes, n => n.Contains("GATE: PASS"));
        Assert.DoesNotContain(result.Notes, n => n.Contains("GATE: FAIL"));
    }

    [Fact]
    public async Task Every_case_is_scored_correct()
    {
        var suite = new CheckGateSuite();
        var result = await suite.RunAsync(new EvalOptions(BenchRoot: Path.GetTempPath()), CancellationToken.None);

        // No case abstains in-process (the compilation is always available), so every task must score 1.0.
        Assert.All(result.Tasks, t => Assert.Equal(1.0, t.Recall, 3));
    }
}
