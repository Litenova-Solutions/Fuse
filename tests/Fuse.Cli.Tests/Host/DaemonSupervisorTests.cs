using Fuse.Cli.Rpc;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// G5: the daemon supervisor's control flow (spawn-on-demand). Tested with injected probe and spawn so the
// decisions are exercised without launching real processes: a running daemon is reused, a missing one is spawned
// and awaited, and a daemon that never comes up is reported as failed rather than hanging.
public sealed class DaemonSupervisorTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task An_already_running_daemon_is_reused_without_spawning()
    {
        var spawned = 0;
        var supervisor = new DaemonSupervisor(_ => Task.FromResult(true), () => spawned++, Fast);

        var outcome = await supervisor.EnsureRunningAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(DaemonSupervisor.Outcome.AlreadyRunning, outcome);
        Assert.Equal(0, spawned); // never spawn when one already serves the root
    }

    [Fact]
    public async Task A_missing_daemon_is_spawned_and_awaited()
    {
        var spawned = 0;
        var probeCalls = 0;
        // Not serving until after the spawn, then serving on a later probe (the daemon coming up).
        Task<bool> Probe(CancellationToken _) => Task.FromResult(spawned > 0 && ++probeCalls >= 2);

        var supervisor = new DaemonSupervisor(Probe, () => spawned++, Fast);
        var outcome = await supervisor.EnsureRunningAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(DaemonSupervisor.Outcome.Started, outcome);
        Assert.Equal(1, spawned); // spawned exactly once
    }

    [Fact]
    public async Task A_daemon_that_never_comes_up_is_reported_as_failed()
    {
        var spawned = 0;
        var supervisor = new DaemonSupervisor(_ => Task.FromResult(false), () => spawned++, Fast);

        var outcome = await supervisor.EnsureRunningAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);

        Assert.Equal(DaemonSupervisor.Outcome.FailedToStart, outcome);
        Assert.Equal(1, spawned); // spawned once, then gave up after the timeout rather than hanging
    }
}
