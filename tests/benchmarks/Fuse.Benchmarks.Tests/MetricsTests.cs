using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

public sealed class MetricsTests
{
    [Fact]
    public void Recall_counts_ground_truth_hits()
    {
        var retrieved = new HashSet<string> { "a", "b", "x" };
        var groundTruth = new[] { "a", "b", "c", "d" };
        Assert.Equal(0.5, Metrics.Recall(retrieved, groundTruth), 3);
    }

    [Fact]
    public void Recall_of_empty_ground_truth_is_one()
        => Assert.Equal(1.0, Metrics.Recall(new HashSet<string>(), Array.Empty<string>()));

    [Fact]
    public void Precision_counts_relevant_returned()
    {
        var retrieved = new[] { "a", "b", "x", "y" };
        var groundTruth = new HashSet<string> { "a", "b", "c" };
        Assert.Equal(0.5, Metrics.Precision(retrieved, groundTruth), 3);
    }

    [Fact]
    public void Precision_of_empty_retrieval_is_zero()
        => Assert.Equal(0.0, Metrics.Precision(Array.Empty<string>(), new HashSet<string> { "a" }));

    [Fact]
    public void F1_is_harmonic_mean()
        => Assert.Equal(0.5, Metrics.F1(0.5, 0.5), 3);

    [Fact]
    public void Median_of_even_count_averages_middle_two()
        => Assert.Equal(2.5, Metrics.Median(new[] { 1.0, 2.0, 3.0, 4.0 }), 3);

    [Fact]
    public void Median_of_odd_count_is_middle()
        => Assert.Equal(3.0, Metrics.Median(new[] { 1.0, 3.0, 100.0 }), 3);

    [Fact]
    public void BootstrapCi_is_deterministic_and_brackets_the_mean()
    {
        var sample = new[] { 0.2, 0.4, 0.6, 0.8, 1.0 };
        var (low1, high1) = Metrics.BootstrapCi(sample);
        var (low2, high2) = Metrics.BootstrapCi(sample);
        Assert.Equal(low1, low2);
        Assert.Equal(high1, high2);
        var mean = Metrics.Mean(sample);
        Assert.True(low1 <= mean && mean <= high1, $"mean {mean} not in [{low1}, {high1}]");
    }

    [Fact]
    public void BootstrapCi_of_single_sample_is_degenerate()
    {
        var (low, high) = Metrics.BootstrapCi(new[] { 0.7 });
        Assert.Equal(0.7, low);
        Assert.Equal(0.7, high);
    }
}
