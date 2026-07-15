using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Xunit;

namespace Fuse.Semantics.Tests;

// R42: the warm-solution cache's reuse, freshness, LRU cap, idle eviction, and concurrency, exercised without an
// SDK by injecting a fake loader (an AdhocWorkspace) and a controllable freshness signature. This proves the
// mechanics the daemon-held warm Solution depends on; the byte-identical refactor/doctor behavior over a real
// MSBuild load is covered by the tolerant integration tests.
public sealed class WarmSolutionCacheTests
{
    // A workspace that records when it is disposed, so a test can prove the cache evicts and disposes it.
    // AdhocWorkspace is sealed, so this derives from Workspace directly; its CurrentSolution is an empty solution.
    private sealed class TrackingWorkspace : Workspace
    {
        public TrackingWorkspace() : base(MefHostServices.DefaultHost, "Tracking") { }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool finalize)
        {
            Disposed = true;
            base.Dispose(finalize);
        }
    }

    private static (Func<string, CancellationToken, Task<LoadedWorkspace>> Loader, List<TrackingWorkspace> Created) CountingLoader()
    {
        var created = new List<TrackingWorkspace>();
        Func<string, CancellationToken, Task<LoadedWorkspace>> loader = (_, _) =>
        {
            var ws = new TrackingWorkspace();
            created.Add(ws);
            return Task.FromResult(new LoadedWorkspace(ws, ws.CurrentSolution, Array.Empty<string>()));
        };
        return (loader, created);
    }

    [Fact]
    public async Task Second_open_of_a_fresh_target_reuses_the_held_solution()
    {
        var (loader, created) = CountingLoader();
        using var cache = new WarmSolutionCache(cap: 3, loader: loader, signature: _ => "v1");

        var first = await cache.OpenAsync("/repo/App.sln", CancellationToken.None);
        var second = await cache.OpenAsync("/repo/App.sln", CancellationToken.None);

        Assert.Equal(1, cache.LoadCount); // The second open did not reload.
        Assert.Equal(1, cache.HeldCount);
        Assert.Single(created);
        Assert.Same(first.Solution, second.Solution); // Byte-identical input: the same immutable snapshot is reused.
    }

    [Fact]
    public async Task Changed_signature_forces_a_reload()
    {
        var (loader, _) = CountingLoader();
        var version = "v1";
        using var cache = new WarmSolutionCache(cap: 3, loader: loader, signature: _ => version);

        await cache.OpenAsync("/repo/App.sln", CancellationToken.None);
        version = "v2"; // An edit changed the tracked source.
        await cache.OpenAsync("/repo/App.sln", CancellationToken.None);

        Assert.Equal(2, cache.LoadCount); // The stale entry was reloaded.
        Assert.Equal(1, cache.HeldCount); // The stale entry was replaced, not accumulated.
    }

    [Fact]
    public async Task Watcher_attached_fresh_hit_skips_the_signature_scan_and_a_change_evicts()
    {
        var (loader, _) = CountingLoader();
        var signatureCalls = 0;
        using var cache = new WarmSolutionCache(
            cap: 3,
            loader: loader,
            signature: _ =>
            {
                Interlocked.Increment(ref signatureCalls);
                return "v1";
            });
        var root = Path.Combine(Path.GetTempPath(), "fuse-warm-cache", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "App.sln");
        using var watcher = cache.AttachWatcher(root);

        var first = await cache.OpenAsync(target, CancellationToken.None);
        var second = await cache.OpenAsync(target, CancellationToken.None);

        Assert.Equal(1, signatureCalls);
        Assert.Equal(1, cache.LoadCount);
        Assert.Same(first.Solution, second.Solution);

        cache.InvalidateWatcherRoot(root);
        await cache.OpenAsync(target, CancellationToken.None);

        Assert.Equal(2, signatureCalls);
        Assert.Equal(2, cache.LoadCount);
    }

    [Fact]
    public async Task Detached_watcher_restores_the_signature_freshness_fallback()
    {
        var (loader, _) = CountingLoader();
        var version = "v1";
        var signatureCalls = 0;
        using var cache = new WarmSolutionCache(
            cap: 3,
            loader: loader,
            signature: _ =>
            {
                Interlocked.Increment(ref signatureCalls);
                return version;
            });
        var root = Path.Combine(Path.GetTempPath(), "fuse-warm-cache", Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "App.sln");

        using (cache.AttachWatcher(root))
        {
            await cache.OpenAsync(target, CancellationToken.None);
            await cache.OpenAsync(target, CancellationToken.None);
        }

        version = "v2";
        await cache.OpenAsync(target, CancellationToken.None);

        Assert.Equal(2, signatureCalls);
        Assert.Equal(2, cache.LoadCount);
    }

    [Fact]
    public void Watcher_invalidation_tracks_only_files_in_the_fallback_signature()
    {
        var root = Path.Combine(Path.GetTempPath(), "fuse-warm-cache", Guid.NewGuid().ToString("N"));

        Assert.True(WarmSolutionCache.IsTrackedSourceFile(root, Path.Combine(root, "App.cs")));
        Assert.False(WarmSolutionCache.IsTrackedSourceFile(root, Path.Combine(root, "obj", "Generated.cs")));
        Assert.False(WarmSolutionCache.IsTrackedSourceFile(root, Path.Combine(root, "bin", "Generated.cs")));
        Assert.False(WarmSolutionCache.IsTrackedSourceFile(root, Path.Combine(root, "README.md")));
        Assert.False(WarmSolutionCache.IsTrackedSourceFile(root, Path.Combine(Path.GetTempPath(), "outside", "App.cs")));
    }

    [Fact]
    public async Task Lru_evicts_the_least_recently_used_beyond_the_cap()
    {
        var (loader, created) = CountingLoader();
        using var cache = new WarmSolutionCache(cap: 2, loader: loader, signature: p => p);

        await cache.OpenAsync("/repo/A.sln", CancellationToken.None); // created[0]
        await cache.OpenAsync("/repo/B.sln", CancellationToken.None); // created[1]
        await cache.OpenAsync("/repo/C.sln", CancellationToken.None); // created[2] -> evicts A (LRU)

        Assert.Equal(2, cache.HeldCount);
        Assert.True(created[0].Disposed); // A was evicted and its workspace disposed.
        Assert.False(created[1].Disposed);
        Assert.False(created[2].Disposed);

        // A re-open of A reloads (it was evicted); B is still warm.
        await cache.OpenAsync("/repo/A.sln", CancellationToken.None);
        Assert.Equal(4, cache.LoadCount);
    }

    [Fact]
    public async Task Touching_a_root_keeps_it_from_being_the_lru_eviction()
    {
        var (loader, created) = CountingLoader();
        using var cache = new WarmSolutionCache(cap: 2, loader: loader, signature: p => p);

        await cache.OpenAsync("/repo/A.sln", CancellationToken.None); // created[0]
        await cache.OpenAsync("/repo/B.sln", CancellationToken.None); // created[1]
        await cache.OpenAsync("/repo/A.sln", CancellationToken.None); // touch A -> B is now LRU
        await cache.OpenAsync("/repo/C.sln", CancellationToken.None); // evicts B, not A

        Assert.False(created[0].Disposed); // A survived (recently touched).
        Assert.True(created[1].Disposed);  // B was the LRU and was evicted.
    }

    [Fact]
    public async Task Idle_window_evicts_a_root_not_touched_within_it()
    {
        var (loader, created) = CountingLoader();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var cache = new WarmSolutionCache(
            cap: 10, idleWindow: TimeSpan.FromMinutes(10), clock: () => now, loader: loader, signature: p => p);

        await cache.OpenAsync("/repo/A.sln", CancellationToken.None);
        now = now.AddMinutes(30); // A is now idle past the window.
        await cache.OpenAsync("/repo/B.sln", CancellationToken.None); // the sweep on this open evicts A

        Assert.True(created[0].Disposed);
        Assert.Equal(1, cache.HeldCount);
    }

    [Fact]
    public async Task Concurrent_opens_of_the_same_target_load_once()
    {
        var gate = new SemaphoreSlim(0, 1);
        var loadCalls = 0;
        Func<string, CancellationToken, Task<LoadedWorkspace>> loader = async (_, _) =>
        {
            Interlocked.Increment(ref loadCalls);
            await gate.WaitAsync(); // Hold the first load open so concurrent callers pile up on the load gate.
            var ws = new TrackingWorkspace();
            return new LoadedWorkspace(ws, ws.CurrentSolution, Array.Empty<string>());
        };
        using var cache = new WarmSolutionCache(cap: 3, loader: loader, signature: _ => "v1");

        var opens = Enumerable.Range(0, 5)
            .Select(_ => cache.OpenAsync("/repo/App.sln", CancellationToken.None))
            .ToArray();
        gate.Release(); // Let the single in-flight load complete; the rest hit the re-checked cache.
        await Task.WhenAll(opens);

        Assert.Equal(1, loadCalls);
        Assert.Equal(1, cache.LoadCount);
    }

    [Fact]
    public async Task Dispose_releases_every_held_workspace()
    {
        var (loader, created) = CountingLoader();
        var cache = new WarmSolutionCache(cap: 10, loader: loader, signature: p => p);
        await cache.OpenAsync("/repo/A.sln", CancellationToken.None);
        await cache.OpenAsync("/repo/B.sln", CancellationToken.None);

        cache.Dispose();

        Assert.All(created, ws => Assert.True(ws.Disposed));
    }

    [Fact]
    public async Task Evict_under_root_releases_only_that_root()
    {
        var (loader, created) = CountingLoader();
        using var cache = new WarmSolutionCache(cap: 10, loader: loader, signature: p => p);
        var root = Path.Combine(Path.GetTempPath(), "fuse-warm-root", Guid.NewGuid().ToString("N"));
        var other = Path.Combine(Path.GetTempPath(), "fuse-warm-other", Guid.NewGuid().ToString("N"));
        await cache.OpenAsync(Path.Combine(root, "App.sln"), CancellationToken.None);
        await cache.OpenAsync(Path.Combine(other, "Other.sln"), CancellationToken.None);

        Assert.Equal(1, cache.EvictUnderRoot(root));
        Assert.True(created[0].Disposed);
        Assert.False(created[1].Disposed);
        Assert.Equal(1, cache.HeldCount);
    }
}
