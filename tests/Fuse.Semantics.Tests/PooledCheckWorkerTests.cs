using System.Text.Json;
using Fuse.Indexing;
using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// R48: the pooled check worker's reuse, LRU cap, idle eviction, and fallback bookkeeping, exercised without an SDK
// by injecting a fake channel. The pooled verdict equalling the spawn-per-call verdict is guaranteed by
// construction (the worker's CheckHeld is the same code CheckFromLog runs) and is exercised end-to-end by the
// check-gate honesty suite; this test covers the pool's start-once/reuse/evict logic.
public sealed class PooledCheckWorkerTests
{
    private sealed class FakeChannel : ICheckWorkerChannel
    {
        private readonly Func<string, string> _respond;
        private readonly bool _throwOnStart;

        public FakeChannel(Func<string, string>? respond = null, bool throwOnStart = false)
        {
            _respond = respond ?? (_ => CleanVerdict());
            _throwOnStart = throwOnStart;
        }

        public bool Disposed { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_throwOnStart)
                throw new InvalidOperationException("worker failed to start");
            return Task.CompletedTask;
        }

        public Task<string> RequestAsync(string requestLine, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(requestLine));

        public void Dispose() => Disposed = true;

        public static string CleanVerdict() =>
            JsonSerializer.Serialize(CheckResult.Ok([]), BuildCaptureJsonContext.Default.CheckResult);
    }

    private static (PooledCheckWorker Pool, List<FakeChannel> Created) PoolWith(
        int cap, TimeSpan? idle = null, Func<DateTime>? clock = null, Func<string, string>? respond = null)
    {
        var created = new List<FakeChannel>();
        var pool = new PooledCheckWorker(cap: cap, idleWindow: idle, clock: clock, channelFactory: _ =>
        {
            var channel = new FakeChannel(respond);
            created.Add(channel);
            return channel;
        });
        return (pool, created);
    }

    [Fact]
    public async Task Second_check_reuses_the_worker_without_restarting()
    {
        var (pool, created) = PoolWith(cap: 2);
        using var _ = pool;

        var first = await pool.TryCheckAsync("/logs/App.complog", "A.cs", "class A {}", CancellationToken.None);
        var second = await pool.TryCheckAsync("/logs/App.complog", "A.cs", "class A {}", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(first!.Verified);
        Assert.Equal(1, pool.SpawnCount); // The second check reused the worker: no restart.
        Assert.Equal(1, pool.HeldCount);
        Assert.Single(created);
    }

    [Fact]
    public async Task Lru_evicts_and_disposes_the_worker_beyond_the_cap()
    {
        var (pool, created) = PoolWith(cap: 1);
        using var _ = pool;

        await pool.TryCheckAsync("/logs/A.complog", "A.cs", "x", CancellationToken.None); // created[0]
        await pool.TryCheckAsync("/logs/B.complog", "B.cs", "y", CancellationToken.None); // created[1] -> evicts A

        Assert.Equal(1, pool.HeldCount);
        Assert.True(created[0].Disposed); // A's worker was stopped.
        Assert.False(created[1].Disposed);
    }

    [Fact]
    public async Task Idle_window_evicts_a_worker_not_used_within_it()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (pool, created) = PoolWith(cap: 10, idle: TimeSpan.FromMinutes(10), clock: () => now);
        using var _ = pool;

        await pool.TryCheckAsync("/logs/A.complog", "A.cs", "x", CancellationToken.None);
        now = now.AddMinutes(30);
        await pool.TryCheckAsync("/logs/B.complog", "B.cs", "y", CancellationToken.None); // the sweep evicts A

        Assert.True(created[0].Disposed);
        Assert.Equal(1, pool.HeldCount);
    }

    [Fact]
    public async Task Start_failure_returns_null_so_the_caller_falls_back()
    {
        using var pool = new PooledCheckWorker(cap: 2, channelFactory: _ => new FakeChannel(throwOnStart: true));
        var result = await pool.TryCheckAsync("/logs/A.complog", "A.cs", "x", CancellationToken.None);
        Assert.Null(result); // Fall back to spawn-per-call.
        Assert.Equal(0, pool.HeldCount);
    }

    [Fact]
    public async Task Verdict_passes_through_from_the_worker()
    {
        var diag = new CheckDiagnostic("CS1002", "Error", "; expected", "A.cs", 3);
        var respond = new Func<string, string>(_ =>
            JsonSerializer.Serialize(CheckResult.Ok([diag]), BuildCaptureJsonContext.Default.CheckResult));
        var (pool, _) = PoolWith(cap: 2, respond: respond);
        using var _p = pool;

        var result = await pool.TryCheckAsync("/logs/A.complog", "A.cs", "class A {", CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.Verified);
        Assert.Contains(result.Diagnostics, d => d.Id == "CS1002");
        Assert.False(result.IsClean); // An error-severity diagnostic means not clean.
    }

    [Fact]
    public async Task Explicit_evict_all_releases_every_held_worker()
    {
        var (pool, created) = PoolWith(cap: 3);
        using var _ = pool;
        await pool.TryCheckAsync("/logs/A.complog", "A.cs", "x", CancellationToken.None);
        await pool.TryCheckAsync("/logs/B.complog", "B.cs", "y", CancellationToken.None);

        Assert.Equal(2, pool.EvictAll());
        Assert.Equal(0, pool.HeldCount);
        Assert.All(created, channel => Assert.True(channel.Disposed));
    }

    [Fact]
    public async Task Root_scoped_eviction_releases_only_that_roots_workers()
    {
        var (pool, created) = PoolWith(cap: 3);
        using var _ = pool;
        var root = Path.Combine(Path.GetTempPath(), "fuse-pooled-root", Guid.NewGuid().ToString("N"));
        var other = Path.Combine(Path.GetTempPath(), "fuse-pooled-other", Guid.NewGuid().ToString("N"));
        await pool.TryCheckAsync("/logs/A.complog", "A.cs", "x", CancellationToken.None, root);
        await pool.TryCheckAsync("/logs/B.complog", "B.cs", "y", CancellationToken.None, other);

        Assert.Equal(1, pool.EvictOwnedBy(root));
        Assert.True(created[0].Disposed);
        Assert.False(created[1].Disposed);
        Assert.Equal(1, pool.HeldCount);
    }
}
