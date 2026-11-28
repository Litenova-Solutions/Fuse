using Fuse.Operations.Build;
namespace Fuse.Tests.Unit;

/// <summary>
///     A build or test run asks the engine to yield memory only when OS memory is tight, so a yield never drops
///     views the engine reloads while the run is still going.
/// </summary>
public class MemoryTightTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(79, 100)]
    [InlineData(7_999, 10_000)]
    public void Roomy_memory_is_not_tight(long load, long total)
    {
        Assert.False(BuildOperation.MemoryTight(load, total));
    }

    [Theory]
    [InlineData(80, 100)]
    [InlineData(8_000, 10_000)]
    [InlineData(100, 100)]
    [InlineData(13_000_000_000, 16_000_000_000)]
    public void Memory_at_or_past_eighty_percent_is_tight(long load, long total)
    {
        Assert.True(BuildOperation.MemoryTight(load, total));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(80, 0)]
    [InlineData(80, -1)]
    public void Unknown_memory_counts_as_roomy(long load, long total)
    {
        Assert.False(BuildOperation.MemoryTight(load, total));
    }
}
