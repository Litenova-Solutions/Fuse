using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// R7: the latency percentile helper used by the performance suite.
public sealed class PerformanceSuiteTests
{
    [Fact]
    public void Percentile_returns_zero_for_empty()
    {
        Assert.Equal(0, PerformanceSuite.Percentile([], 50));
    }

    [Fact]
    public void Percentile_returns_the_single_value()
    {
        Assert.Equal(7.0, PerformanceSuite.Percentile([7.0], 95));
    }

    [Theory]
    [InlineData(50, 3.0)]
    [InlineData(0, 1.0)]
    [InlineData(100, 5.0)]
    public void Percentile_interpolates_over_a_sorted_sample(double percentile, double expected)
    {
        // Unsorted input; the helper sorts internally. Median of 1..5 is 3; p0 is 1; p100 is 5.
        var sample = new List<double> { 5.0, 1.0, 3.0, 2.0, 4.0 };
        Assert.Equal(expected, PerformanceSuite.Percentile(sample, percentile), 3);
    }

    [Fact]
    public void Percentile_p95_lands_between_the_top_two_for_a_small_sample()
    {
        var sample = new List<double> { 10, 20, 30, 40 };
        var p95 = PerformanceSuite.Percentile(sample, 95);
        Assert.InRange(p95, 30, 40);
    }
}
