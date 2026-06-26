using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction.Tokenization;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class TokenCostModelTests
{
    // A deterministic counter (one token per character) so the assertions are exact.
    private sealed class CharCounter : ITokenCounter
    {
        public int Count(string content) => content.Length;
    }

    private readonly DefaultTokenCostModel _model = new(new CharCounter());

    [Fact]
    public void CountTokens_DelegatesToCounter()
    {
        Assert.Equal(5, _model.CountTokens("hello"));
        Assert.Equal(0, _model.CountTokens(""));
    }

    [Fact]
    public void EstimateReducedTokens_SkeletonCostsFarLessThanFullBody()
    {
        var content = new string('x', 1000);
        var full = _model.EstimateReducedTokens(content, ".cs", ReductionLevel.None);
        var skeleton = _model.EstimateReducedTokens(content, ".cs", ReductionLevel.Skeleton);

        Assert.True(skeleton < full);
        Assert.True(skeleton < full / 2, "a skeleton should cost much less than half a full body");
    }

    [Theory]
    [InlineData(ReductionLevel.None, 920)]
    [InlineData(ReductionLevel.Standard, 920)]
    [InlineData(ReductionLevel.Aggressive, 700)]
    [InlineData(ReductionLevel.Skeleton, 150)]
    [InlineData(ReductionLevel.PublicApi, 100)]
    public void EstimateReducedTokens_AppliesCalibratedRetentionForCSharp(ReductionLevel level, int expected)
    {
        var content = new string('x', 1000);
        Assert.Equal(expected, _model.EstimateReducedTokens(content, ".cs", level));
    }

    [Fact]
    public void EstimateReducedTokens_NonCSharpRetainsAlmostEverythingAtEveryLevel()
    {
        var content = new string('x', 1000);
        Assert.Equal(950, _model.EstimateReducedTokens(content, ".json", ReductionLevel.Skeleton));
        Assert.Equal(950, _model.EstimateReducedTokens(content, ".json", ReductionLevel.None));
    }

    [Fact]
    public void EstimateReducedTokens_EmptyContent_IsZero()
    {
        Assert.Equal(0, _model.EstimateReducedTokens("", ".cs", ReductionLevel.None));
    }
}
