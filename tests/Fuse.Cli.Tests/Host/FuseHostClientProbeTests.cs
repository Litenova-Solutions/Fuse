using Fuse.Cli.Rpc;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// G5: the daemon probes never throw for the absence of a daemon. For a root no daemon serves, IsServingAsync is
// false and TryStatsAsync is null, so the status line and the supervisor treat "no daemon" cleanly rather than
// erroring. Uses a unique root so a real daemon cannot be serving it.
public sealed class FuseHostClientProbeTests
{
    private static string UniqueRoot() =>
        Path.Combine(Path.GetTempPath(), "fuse-probe-test", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task IsServingAsync_is_false_when_no_daemon_serves_the_root()
    {
        var serving = await FuseHostClient.IsServingAsync(UniqueRoot(), TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.False(serving);
    }

    [Fact]
    public async Task TryStatsAsync_is_null_when_no_daemon_serves_the_root()
    {
        var stats = await FuseHostClient.TryStatsAsync(UniqueRoot(), TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.Null(stats);
    }
}
