using Fuse.Fusion.Scoping;

namespace Fuse.Fusion.Tests.Scoping;

public sealed class RelevanceIndexCacheTests
{
    [Fact]
    public void GetOrBuild_SameSignature_BuildsOnceAndReturnsSameInstance()
    {
        var cache = new RelevanceIndexCache();
        var builds = 0;
        IRelevanceIndex Build() { builds++; return new Bm25RelevanceIndex(); }

        var first = cache.GetOrBuild(42, Build);
        var second = cache.GetOrBuild(42, Build);

        Assert.Same(first, second);
        Assert.Equal(1, builds);
    }

    [Fact]
    public void GetOrBuild_DifferentSignature_Rebuilds()
    {
        var cache = new RelevanceIndexCache();
        var builds = 0;
        IRelevanceIndex Build() { builds++; return new Bm25RelevanceIndex(); }

        var first = cache.GetOrBuild(1, Build);
        var second = cache.GetOrBuild(2, Build);

        Assert.NotSame(first, second);
        Assert.Equal(2, builds);
    }

    [Fact]
    public void GetOrBuild_SingleEntry_RebuildsWhenSignatureReturns()
    {
        var cache = new RelevanceIndexCache();
        var builds = 0;
        IRelevanceIndex Build() { builds++; return new Bm25RelevanceIndex(); }

        cache.GetOrBuild(1, Build);
        cache.GetOrBuild(2, Build); // evicts signature 1 (single entry)
        cache.GetOrBuild(1, Build); // rebuilt

        Assert.Equal(3, builds);
    }
}
