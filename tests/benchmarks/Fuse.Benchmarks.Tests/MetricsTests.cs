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

    [Fact]
    public void ReciprocalRank_is_one_over_first_hit_rank()
    {
        var gt = new HashSet<string> { "b", "d" };
        // First hit "b" is at rank 2 (1-based) -> 1/2.
        Assert.Equal(0.5, Metrics.ReciprocalRank(new[] { "a", "b", "c", "d" }, gt), 6);
    }

    [Fact]
    public void ReciprocalRank_is_zero_when_no_hit()
        => Assert.Equal(0.0, Metrics.ReciprocalRank(new[] { "a", "b" }, new HashSet<string> { "z" }));

    [Fact]
    public void RecallAtK_counts_ground_truth_in_top_k()
    {
        var ranked = new[] { "a", "x", "b", "y", "c" };
        var gt = new[] { "a", "b", "c" };
        Assert.Equal(1.0 / 3, Metrics.RecallAtK(ranked, gt, 1), 6); // only "a" in top 1
        Assert.Equal(2.0 / 3, Metrics.RecallAtK(ranked, gt, 3), 6); // "a","b" in top 3
        Assert.Equal(1.0, Metrics.RecallAtK(ranked, gt, 5), 6);     // all three in top 5
    }

    [Fact]
    public void RecallAtK_of_empty_ground_truth_is_one()
        => Assert.Equal(1.0, Metrics.RecallAtK(new[] { "a" }, Array.Empty<string>(), 10));

    [Fact]
    public void NdcgAtK_is_one_when_all_relevant_are_at_the_top()
    {
        var gt = new HashSet<string> { "a", "b" };
        Assert.Equal(1.0, Metrics.NdcgAtK(new[] { "a", "b", "c" }, gt, 10), 6);
    }

    [Fact]
    public void NdcgAtK_penalizes_lower_placement()
    {
        var gt = new HashSet<string> { "a" };
        var top = Metrics.NdcgAtK(new[] { "a", "b", "c" }, gt, 10);
        var lower = Metrics.NdcgAtK(new[] { "b", "c", "a" }, gt, 10);
        Assert.Equal(1.0, top, 6);
        Assert.True(lower < top, $"expected lower placement to score below top ({lower} vs {top})");
        // Single relevant at rank 3: DCG = 1/log2(4) = 0.5, IDCG = 1 -> nDCG = 0.5.
        Assert.Equal(0.5, lower, 6);
    }
}
