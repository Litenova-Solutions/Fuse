using Fuse.Cli.Services;
using Xunit;

namespace Fuse.Cli.Tests.Services;

// R39: the live watcher reconciles the index on a debounced change, overlap-guarded and best-effort, so reads
// stay fresh with no per-read cost. Default-on with a FUSE_WATCH=0 opt-out.
public sealed class LiveIndexWatcherTests
{
    [Fact]
    public void IsEnabled_DefaultsOn_AndOptsOut()
    {
        var original = Environment.GetEnvironmentVariable(LiveIndexWatcher.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(LiveIndexWatcher.EnvVar, null);
            Assert.True(LiveIndexWatcher.IsEnabled());
            Environment.SetEnvironmentVariable(LiveIndexWatcher.EnvVar, "0");
            Assert.False(LiveIndexWatcher.IsEnabled());
            Environment.SetEnvironmentVariable(LiveIndexWatcher.EnvVar, "off");
            Assert.False(LiveIndexWatcher.IsEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveIndexWatcher.EnvVar, original);
        }
    }

    [Fact]
    public async Task HandleChange_RunsTheReconcile()
    {
        var count = 0;
        using var watcher = new LiveIndexWatcher(_ => { Interlocked.Increment(ref count); return Task.CompletedTask; }, null, CancellationToken.None);

        await watcher.HandleChangeAsync(CancellationToken.None);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ConcurrentChanges_AreOverlapGuarded_OneReconcileAtATime()
    {
        var count = 0;
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        using var watcher = new LiveIndexWatcher(
            async _ =>
            {
                Interlocked.Increment(ref count);
                started.TrySetResult();
                await gate.Task;
            },
            null,
            CancellationToken.None);

        var first = watcher.HandleChangeAsync(CancellationToken.None);
        await started.Task; // the first reconcile is running and awaiting the gate.

        // A change during a reconcile is a no-op (the in-flight pass covers it), so the reconcile ran only once.
        await watcher.HandleChangeAsync(CancellationToken.None);
        Assert.Equal(1, count);

        gate.SetResult();
        await first;

        // After it completes, a later change reconciles again.
        await watcher.HandleChangeAsync(CancellationToken.None);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task ReconcileFailure_IsSwallowed_AndDoesNotWedge()
    {
        var attempts = 0;
        using var watcher = new LiveIndexWatcher(
            _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new IOException("transient");
            },
            null,
            CancellationToken.None);

        await watcher.HandleChangeAsync(CancellationToken.None); // must not throw.
        await watcher.HandleChangeAsync(CancellationToken.None); // the guard reset, so it runs again.

        Assert.Equal(2, attempts);
    }
}
