using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction.Caching;

namespace Fuse.Reduction.Tests;

public sealed class ReductionLevelTests
{
    // (level, comments, usings, namespaces, regions, aggressive, skeleton, publicApi)
    public static TheoryData<ReductionLevel, bool, bool, bool, bool, bool, bool, bool> LevelTransforms => new()
    {
        { ReductionLevel.None, false, false, false, false, false, false, false },
        { ReductionLevel.Standard, true, true, true, true, false, false, false },
        { ReductionLevel.Aggressive, true, true, true, true, true, false, false },
        { ReductionLevel.Skeleton, false, false, false, false, false, true, false },
        { ReductionLevel.PublicApi, false, false, false, false, false, true, true },
    };

    [Theory]
    [MemberData(nameof(LevelTransforms))]
    public void Level_DerivesExpectedTransformSet(
        ReductionLevel level,
        bool comments,
        bool usings,
        bool namespaces,
        bool regions,
        bool aggressive,
        bool skeleton,
        bool publicApi)
    {
        var options = new ReductionOptions(level: level);

        Assert.Equal(comments, options.RemoveCSharpComments);
        Assert.Equal(usings, options.RemoveCSharpUsings);
        Assert.Equal(namespaces, options.RemoveCSharpNamespaces);
        Assert.Equal(regions, options.RemoveCSharpRegions);
        Assert.Equal(aggressive, options.AggressiveCSharpReduction);
        Assert.Equal(skeleton, options.SkeletonMode);
        Assert.Equal(publicApi, options.PublicApiMode);
    }

    [Fact]
    public void DefaultLevel_IsNone()
    {
        Assert.Equal(ReductionLevel.None, new ReductionOptions().Level);
    }

    [Fact]
    public void OrthogonalFlags_AreIndependentOfLevel()
    {
        var options = new ReductionOptions(
            level: ReductionLevel.Skeleton,
            collapseGeneratedCode: true,
            includeRouteMap: true,
            enableRedaction: false);

        Assert.True(options.CollapseGeneratedCode);
        Assert.True(options.IncludeRouteMap);
        Assert.False(options.EnableRedaction);
        Assert.True(options.SkeletonMode);
    }

    [Fact]
    public void Hash_IsStableForEquivalentOptions()
    {
        var a = new ReductionOptions(level: ReductionLevel.Aggressive);
        var b = new ReductionOptions(level: ReductionLevel.Aggressive);

        Assert.Equal(
            ReductionHasher.HashReductionOptions(".cs", a),
            ReductionHasher.HashReductionOptions(".cs", b));
    }

    [Fact]
    public void Hash_DiffersByLevel()
    {
        var standard = ReductionHasher.HashReductionOptions(".cs", new ReductionOptions(level: ReductionLevel.Standard));
        var aggressive = ReductionHasher.HashReductionOptions(".cs", new ReductionOptions(level: ReductionLevel.Aggressive));

        Assert.NotEqual(standard, aggressive);
    }
}
