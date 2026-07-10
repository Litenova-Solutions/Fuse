using Fuse.Cli.Rpc;
using Xunit;

namespace Fuse.Cli.Tests.Host;

// G5: the single-instance daemon lock. A spawn race for one root must resolve to exactly one owner (the daemon);
// the losers connect as clients instead. Cross-thread contention stands in for cross-process here (a named
// mutex is thread-owned, so a second thread contending is the same arbitration a second process sees).
public sealed class DaemonLockTests
{
    [Fact]
    public void A_race_for_one_root_yields_exactly_one_owner()
    {
        // A unique root string per test run so the named mutex does not collide with a real daemon or another test.
        var root = Path.Combine(Path.GetTempPath(), "fuse-daemon-test", Guid.NewGuid().ToString("N"));

        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        DaemonLock? first = null;

        var owner = new Thread(() =>
        {
            first = DaemonLock.TryAcquire(root);
            acquired.Set();
            release.Wait();
            first.Dispose();
        })
        { IsBackground = true };
        owner.Start();

        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)), "the owner thread should acquire the lock");
        Assert.True(first!.IsOwner, "the first acquirer wins the lock");

        // A concurrent acquirer while the owner holds the lock does NOT win - it must become a client.
        using (var contender = DaemonLock.TryAcquire(root))
            Assert.False(contender.IsOwner, "a second acquirer must not also own the daemon lock");

        // After the owner releases, a fresh acquirer wins - a restarted daemon can take over the root.
        release.Set();
        Assert.True(owner.Join(TimeSpan.FromSeconds(5)), "the owner thread should exit after releasing");
        using (var next = DaemonLock.TryAcquire(root))
            Assert.True(next.IsOwner, "after release, a new process can become the daemon");
    }

    [Fact]
    public void Different_roots_get_independent_locks()
    {
        var rootA = Path.Combine(Path.GetTempPath(), "fuse-daemon-test", Guid.NewGuid().ToString("N"));
        var rootB = Path.Combine(Path.GetTempPath(), "fuse-daemon-test", Guid.NewGuid().ToString("N"));
        using var a = DaemonLock.TryAcquire(rootA);
        using var b = DaemonLock.TryAcquire(rootB);
        Assert.True(a.IsOwner);
        Assert.True(b.IsOwner); // distinct roots never contend
    }
}
