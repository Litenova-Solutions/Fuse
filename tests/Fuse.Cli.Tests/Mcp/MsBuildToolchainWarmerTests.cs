using System.Diagnostics;
using Fuse.Cli.Mcp;
using Fuse.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host.Mef;
using Xunit;

namespace Fuse.Cli.Tests.Mcp;

// R44: the MSBuild toolchain warmer primes the R42 warm-solution cache in the background at startup, without an
// SDK (a fake, delaying loader stands in for MSBuildWorkspace). It proves the warmup ran at startup (a metric),
// primed the cache (so the first refactor/doctor is warm), returned without blocking, and honors the opt-out.
public sealed class MsBuildToolchainWarmerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-r44", Guid.NewGuid().ToString("N"));

    public MsBuildToolchainWarmerTests()
    {
        Directory.CreateDirectory(_root);
        // A lone project so discovery returns a target for the warmup to prime.
        File.WriteAllText(Path.Combine(_root, "Foo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
    }

    private sealed class FakeWorkspace : Microsoft.CodeAnalysis.Workspace
    {
        public FakeWorkspace() : base(MefHostServices.DefaultHost, "Fake") { }
    }

    private static (WarmSolutionCache Cache, Func<Task> WaitLoaded) DelayingCache(TimeSpan delay)
    {
        var started = new TaskCompletionSource();
        Func<string, CancellationToken, Task<LoadedWorkspace>> loader = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(delay, ct);
            var ws = new FakeWorkspace();
            return new LoadedWorkspace(ws, ws.CurrentSolution, Array.Empty<string>());
        };
        var cache = new WarmSolutionCache(cap: 3, loader: loader, signature: p => p);
        return (cache, () => started.Task);
    }

    [Fact]
    public async Task Warmup_primes_the_cache_in_the_background_without_blocking_startup()
    {
        var (cache, _) = DelayingCache(TimeSpan.FromMilliseconds(300));
        var startedBefore = MsBuildToolchainWarmer.WarmupsStarted;

        var sw = Stopwatch.StartNew();
        var task = MsBuildToolchainWarmer.Start(_root, cache);
        sw.Stop();

        Assert.NotNull(task); // Enabled by default.
        Assert.True(sw.ElapsedMilliseconds < 200, $"Start blocked for {sw.ElapsedMilliseconds}ms (should return immediately)");

        await task!; // Let the background warmup complete.
        Assert.Equal(1, cache.LoadCount); // The cache was primed, so the first refactor/doctor is warm.
        Assert.True(MsBuildToolchainWarmer.WarmupsStarted > startedBefore);
    }

    [Fact]
    public void Warmup_returns_null_when_opted_out()
    {
        var previous = Environment.GetEnvironmentVariable(MsBuildToolchainWarmer.EnvVar);
        Environment.SetEnvironmentVariable(MsBuildToolchainWarmer.EnvVar, "0");
        try
        {
            var (cache, _) = DelayingCache(TimeSpan.Zero);
            Assert.Null(MsBuildToolchainWarmer.Start(_root, cache));
            Assert.Equal(0, cache.LoadCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MsBuildToolchainWarmer.EnvVar, previous);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
