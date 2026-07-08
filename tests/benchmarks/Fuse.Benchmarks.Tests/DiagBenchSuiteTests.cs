using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// Suite H2 (DiagBench): the apply-and-recheck loop must produce API-shape mutants, attach the shipped repair
// packet's machine-applicable TopRepair, auto-apply it, and recompile - recording a fix rate per diagnostic id.
// This runs the deterministic in-process core (raw Roslyn + a directly-populated store), so it needs no corpus.
public sealed class DiagBenchSuiteTests
{
    [Fact]
    public async Task Produces_packeted_mutants_and_records_a_fix_rate()
    {
        var suite = new DiagBenchSuite();
        var options = new EvalOptions(BenchRoot: Path.GetTempPath());

        var result = await suite.RunAsync(options, CancellationToken.None);

        Assert.Equal("diagbench", result.Suite);
        Assert.NotEmpty(result.Tasks);
        // The overall note records the auto-fix count over the packeted mutants; both classes are exercised.
        Assert.Contains(result.Notes, n => n.Contains("overall:") && n.Contains("auto-fixed"));
        Assert.Contains(result.Notes, n => n.StartsWith("CS1061:"));
        Assert.Contains(result.Notes, n => n.StartsWith("CS0246:"));
    }

    [Fact]
    public async Task Every_packeted_near_miss_is_repaired_by_the_top_repair()
    {
        // A single-character near-miss of a real member or type name has its original as the nearest recorded
        // name, so the packet's TopRepair recovers it and the recompile is clean: the fix rate is 1.0 here, and a
        // regression that broke the nearest-name repair or the TopRepair field would drop it below 1.0.
        var suite = new DiagBenchSuite();
        var result = await suite.RunAsync(new EvalOptions(BenchRoot: Path.GetTempPath()), CancellationToken.None);

        Assert.Equal(1.0, result.Scorecard.Recall, 3);   // auto-fix rate of packeted mutants
        Assert.Equal(1.0, result.Scorecard.Precision, 3); // packet coverage of the mutants (every one carried a repair)
    }
}
