using Fuse.Semantics;
using Xunit;

namespace Fuse.Semantics.Tests;

// R48 end-to-end: over a real captured compiler log, the pooled worker's --serve-check protocol answers a second
// check without restarting (SpawnCount stays 1) and its verdict equals the spawn-per-call CheckFromComplogAsync
// verdict. Tolerant: skips when the SDK or the build-capture worker is unavailable, or the tiny project cannot be
// captured in this environment.
public sealed class PooledCheckWorkerIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-r48", Guid.NewGuid().ToString("N"));

    public PooledCheckWorkerIntegrationTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Proj.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "A.cs"), "namespace Demo; public class A { public int X() => 1; }");
    }

    [Fact]
    [Trait("Category", "RequiresSdk")]
    public async Task Pooled_check_reuses_the_worker_and_matches_spawn_per_call()
    {
        var workerPath = BuildCaptureClient.ResolveWorkerPath();
        if (workerPath is null || !File.Exists(workerPath))
            return; // Worker not available in this environment.

        var client = new BuildCaptureClient(workerPath);
        var complog = Path.Combine(_root, "captured.complog");
        var bundle = await client.CaptureBundleAsync(
            Path.Combine(_root, "Proj.csproj"), complog, TimeSpan.FromMinutes(5), CancellationToken.None, _root);
        if (!bundle.Succeeded || !File.Exists(complog))
            return; // Could not build/capture in this environment.

        // A clean edit and a broken edit, checked both ways.
        const string clean = "namespace Demo; public class A { public int X() => 2; }";
        const string broken = "namespace Demo; public class A { public int X() => ; }"; // syntax error

        using var pool = new PooledCheckWorker(cap: 2, channelFactory: null); // real worker channel (env-resolved)

        var pooledClean1 = await pool.TryCheckAsync(complog, "A.cs", clean, CancellationToken.None);
        var pooledClean2 = await pool.TryCheckAsync(complog, "A.cs", clean, CancellationToken.None);
        Assert.NotNull(pooledClean1);
        Assert.Equal(1, pool.SpawnCount); // The second check reused the worker: no re-rehydrate.

        var pooledBroken = await pool.TryCheckAsync(complog, "A.cs", broken, CancellationToken.None);
        Assert.Equal(1, pool.SpawnCount); // Still one worker.

        var spawnClean = await client.CheckFromComplogAsync(complog, "A.cs", clean, TimeSpan.FromMinutes(2), CancellationToken.None);
        var spawnBroken = await client.CheckFromComplogAsync(complog, "A.cs", broken, TimeSpan.FromMinutes(2), CancellationToken.None);

        // Identical verdicts between the pooled worker and the spawn-per-call path.
        Assert.Equal(spawnClean.Verified, pooledClean1!.Verified);
        Assert.Equal(spawnClean.IsClean, pooledClean1.IsClean);
        Assert.True(pooledClean1.IsClean); // The clean edit typechecks clean.

        Assert.NotNull(pooledBroken);
        Assert.Equal(spawnBroken.IsClean, pooledBroken!.IsClean);
        Assert.False(pooledBroken.IsClean); // The broken edit is caught (false-green preserved).
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
