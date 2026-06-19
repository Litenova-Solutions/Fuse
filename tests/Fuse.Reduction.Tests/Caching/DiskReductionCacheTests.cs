using Fuse.Reduction.Caching;

namespace Fuse.Reduction.Tests.Caching;

public sealed class DiskReductionCacheTests : IDisposable
{
    private readonly string _root;
    private readonly DiskReductionCache _cache;

    public DiskReductionCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-cache-tests", Guid.NewGuid().ToString("N"));
        _cache = new DiskReductionCache(_root);
    }

    [Fact]
    public void TryGet_OnEmptyCache_RecordsMiss()
    {
        var found = _cache.TryGet(0x1234UL, 0xABCDUL, out var content);

        Assert.False(found);
        Assert.Empty(content);
        Assert.Equal(0, _cache.Statistics.Hits);
        Assert.Equal(1, _cache.Statistics.Misses);
    }

    [Fact]
    public void Set_ThenTryGet_RecordsHit()
    {
        const ulong contentHash = 0x1111111111111111UL;
        const ulong optionsHash = 0x2222222222222222UL;
        const string reduced = "reduced-content";

        _cache.Set(contentHash, optionsHash, reduced);

        var found = _cache.TryGet(contentHash, optionsHash, out var content);

        Assert.True(found);
        Assert.Equal(reduced, content);
        Assert.Equal(1, _cache.Statistics.Hits);
        Assert.Equal(0, _cache.Statistics.Misses);
    }

    [Fact]
    public void SecondRun_WithSameHashes_ProducesHitsOnly()
    {
        const ulong contentHash = 0xAAAAAAAAAAAAAAAAUL;
        const ulong optionsHash = 0xBBBBBBBBBBBBBBBBUL;

        _cache.Set(contentHash, optionsHash, "payload");

        _cache.TryGet(contentHash, optionsHash, out _);
        _cache.TryGet(contentHash, optionsHash, out _);

        Assert.Equal(2, _cache.Statistics.Hits);
        Assert.Equal(0, _cache.Statistics.Misses);
    }

    [Fact]
    public void Clear_RemovesCachedEntries()
    {
        const ulong contentHash = 0xCCCCCCCCCCCCCCCCUL;
        const ulong optionsHash = 0xDDDDDDDDDDDDDDDDUL;

        _cache.Set(contentHash, optionsHash, "payload");
        _cache.Clear();

        var found = _cache.TryGet(contentHash, optionsHash, out _);

        Assert.False(found);
        Assert.Equal(0, _cache.Statistics.Hits);
        Assert.Equal(1, _cache.Statistics.Misses);
    }

  [Fact]
    public void CacheDirectory_IsUnderFuseFolder()
    {
        _cache.Set(1, 2, "x");

        var cacheDir = Path.Combine(_root, ".fuse", "cache");
        Assert.True(Directory.Exists(cacheDir));
        Assert.NotEmpty(Directory.EnumerateFiles(cacheDir));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
