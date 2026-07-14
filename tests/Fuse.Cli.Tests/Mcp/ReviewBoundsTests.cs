using Fuse.Cli.Mcp;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// R26: fuse_review bounds a large diff. Above the changed-file cap it returns the changed-file list plus a
// narrow-the-base-ref note instead of running unbounded blast-radius resolution; a normal diff is unaffected.
public sealed class ReviewBoundsTests
{
    [Fact]
    public void ShouldBound_LargeDiff_True_NormalDiff_False()
    {
        Assert.True(ReviewBounds.ShouldBound(changedCount: 200, cap: ReviewBounds.DefaultChangedFileCap));
        Assert.False(ReviewBounds.ShouldBound(changedCount: 12, cap: ReviewBounds.DefaultChangedFileCap));
    }

    [Fact]
    public void ResolveCap_PrefersExplicit_ThenDefault()
    {
        Assert.Equal(25, ReviewBounds.ResolveCap(explicitCap: 25));
        Assert.Equal(ReviewBounds.DefaultChangedFileCap, ReviewBounds.ResolveCap(explicitCap: 0));
        Assert.Equal(ReviewBounds.DefaultChangedFileCap, ReviewBounds.ResolveCap(explicitCap: -5));
    }

    [Fact]
    public void ResolveCap_HonorsEnvironmentOverride()
    {
        var original = Environment.GetEnvironmentVariable(ReviewBounds.CapEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ReviewBounds.CapEnvVar, "42");
            Assert.Equal(42, ReviewBounds.ResolveCap(explicitCap: 0));
            Assert.Equal(10, ReviewBounds.ResolveCap(explicitCap: 10)); // explicit still wins over env.
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReviewBounds.CapEnvVar, original);
        }
    }

    [Fact]
    public void FormatBoundedReview_NamesSizeCapAndRemedy_AndListsFiles()
    {
        var files = Enumerable.Range(0, 250).Select(i => $"src/File{i}.cs").ToList();

        var text = ReviewBounds.FormatBoundedReview(
            availabilityHeader: "index_state: ready", changedSince: "main", changedFiles: files, cap: 150);

        Assert.Contains("index_state: ready", text, StringComparison.Ordinal);
        Assert.Contains("spans 250 changed files", text, StringComparison.Ordinal);
        Assert.Contains("above the review cap of 150", text, StringComparison.Ordinal);
        Assert.Contains("Narrow the base ref", text, StringComparison.Ordinal);
        Assert.Contains(ReviewBounds.CapEnvVar, text, StringComparison.Ordinal);
        Assert.Contains("src/File0.cs", text, StringComparison.Ordinal);
        // The listing itself is capped so the partial stays small.
        Assert.Contains("and 50 more", text, StringComparison.Ordinal);
    }
}
