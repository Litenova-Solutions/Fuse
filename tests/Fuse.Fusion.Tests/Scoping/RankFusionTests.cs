using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class RankFusionTests
{
    private static RankedFile R(string path, double score = 1.0) => new(path, score);

    [Fact]
    public void Fuse_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(RankFusion.Fuse([], 10));
        Assert.Empty(RankFusion.Fuse([[]], 10));
    }

    [Fact]
    public void Fuse_SingleRanking_PreservesOrder()
    {
        var ranking = new[] { R("a"), R("b"), R("c") };
        var fused = RankFusion.Fuse([ranking], 10);
        Assert.Equal(["a", "b", "c"], fused.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void Fuse_AgreementAcrossRankings_OutranksSingleTopHit()
    {
        // "b" is ranked 2nd by both lists; "a" is 1st by one and absent from the other. With RRF, the file two
        // variants agree on should beat one variant's lone top pick.
        var first = new[] { R("a"), R("b"), R("c") };
        var second = new[] { R("d"), R("b"), R("c") };

        var fused = RankFusion.Fuse([first, second], 10);

        Assert.Equal("b", fused[0].Path);
    }

    [Fact]
    public void Fuse_IgnoresNullRankings()
    {
        var ranking = new[] { R("a"), R("b") };
        var fused = RankFusion.Fuse([ranking, null], 10);
        Assert.Equal(["a", "b"], fused.Select(f => f.Path).ToArray());
    }

    [Fact]
    public void Fuse_UnionsPathsAcrossRankings()
    {
        var first = new[] { R("a"), R("b") };
        var second = new[] { R("c"), R("d") };
        var fused = RankFusion.Fuse([first, second], 10);
        Assert.Equal(4, fused.Count);
        Assert.Contains("c", fused.Select(f => f.Path));
    }

    [Fact]
    public void Fuse_RespectsTopN()
    {
        var ranking = new[] { R("a"), R("b"), R("c"), R("d") };
        var fused = RankFusion.Fuse([ranking], 2);
        Assert.Equal(2, fused.Count);
    }

    [Fact]
    public void Fuse_IsDeterministicAcrossEqualScores()
    {
        // Two disjoint single-element rankings tie on RRF score; order must be stable (best rank, then path).
        var a = RankFusion.Fuse([[R("zeta")], [R("alpha")]], 10);
        var b = RankFusion.Fuse([[R("zeta")], [R("alpha")]], 10);
        Assert.Equal(a.Select(f => f.Path).ToArray(), b.Select(f => f.Path).ToArray());
        Assert.Equal("alpha", a[0].Path); // equal rank and score -> path tiebreak
    }
}
