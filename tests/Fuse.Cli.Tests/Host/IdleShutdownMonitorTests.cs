using Fuse.Cli.Rpc;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// G5: the daemon idle-shutdown watch. An idle daemon (no connected clients for the window) shuts itself down; a
// daemon with a live client stays up; a zero window disables the watch (a manually run host never self-stops).
// Injected connection count and shutdown action make the timing testable without a running host.
public sealed class IdleShutdownMonitorTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task An_idle_daemon_shuts_itself_down_after_the_window()
    {
        var shutdowns = 0;
        var monitor = new IdleShutdownMonitor(() => 0, () => shutdowns++, idleWindow: TimeSpan.FromMilliseconds(50), Poll);

        await monitor.RunAsync(CancellationToken.None);

        Assert.Equal(1, shutdowns); // no clients for the window -> shut down exactly once
    }

    [Fact]
    public async Task A_busy_daemon_does_not_shut_down()
    {
        var shutdowns = 0;
        using var cts = new CancellationTokenSource();
        // Always one connected client; the watch must never trigger. Stop it after a few windows elapse.
        var monitor = new IdleShutdownMonitor(() => 1, () => shutdowns++, idleWindow: TimeSpan.FromMilliseconds(30), Poll);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await monitor.RunAsync(cts.Token);

        Assert.Equal(0, shutdowns);
    }

    [Fact]
    public async Task A_zero_window_disables_the_monitor()
    {
        var shutdowns = 0;
        var monitor = new IdleShutdownMonitor(() => 0, () => shutdowns++, idleWindow: TimeSpan.Zero, Poll);
        Assert.False(monitor.IsEnabled);

        await monitor.RunAsync(CancellationToken.None); // returns immediately

        Assert.Equal(0, shutdowns);
    }
}
