using Fuse.Testing.Caching;
using Fuse.Testing.Model.Caching;
namespace Fuse.Tests.Unit;

public sealed class SliceTestCacheTests : IDisposable
{
    private readonly string _stateDir = Path.Combine(Path.GetTempPath(), "fuse-slice-cache-tests", Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        if (Directory.Exists(_stateDir))
            Directory.Delete(_stateDir, recursive: true);
    }

    [Fact]
    public void ComputeKey_is_deterministic_order_independent_and_invalidated_by_any_dimension()
    {
        var baseInputs = new SliceTestCache.KeyInputs(
            "tests/SliceA.Tests/SliceA.Tests.csproj",
            "net10.0",
            "peA:pdbA",
            [("src/Core/Core.csproj|net10.0", "core1"), ("buildonly:src/Host/Host.csproj|net10.0", "host1")],
            [("tests/SliceA.Tests/fixture.json", "json1")],
            "rs1",
            SliceTestCache.HashFilter("FullyQualifiedName~SliceA", []));

        var reordered = baseInputs with
        {
            ClosureDependencyHashes = [("buildonly:src/Host/Host.csproj|net10.0", "host1"), ("src/Core/Core.csproj|net10.0", "core1")],
        };

        var baselineKey = SliceTestCache.ComputeKey(baseInputs);
        Assert.Equal(64, baselineKey.Length);
        Assert.Equal(baselineKey, SliceTestCache.ComputeKey(reordered));

        Assert.NotEqual(baselineKey, SliceTestCache.ComputeKey(baseInputs with { TargetFramework = "net11.0" }));
        Assert.NotEqual(baselineKey, SliceTestCache.ComputeKey(baseInputs with { TestProjectEmitHash = "peA2:pdbA" }));
        Assert.NotEqual(baselineKey, SliceTestCache.ComputeKey(baseInputs with { ClosureDependencyHashes = [("src/Core/Core.csproj|net10.0", "core2"), ("buildonly:src/Host/Host.csproj|net10.0", "host1")] }));
        Assert.NotEqual(baselineKey, SliceTestCache.ComputeKey(baseInputs with { ClosureDependencyHashes = [("src/Core/Core.csproj|net10.0", "core1"), ("buildonly:src/Host/Host.csproj|net10.0", "host2")] }));
        Assert.NotEqual(baselineKey, SliceTestCache.ComputeKey(baseInputs with { NonSourceHashes = [("tests/SliceA.Tests/fixture.json", "json2")] }));
        Assert.NotEqual(baselineKey, SliceTestCache.ComputeKey(baseInputs with { RunSettingsHash = "rs2" }));
        Assert.NotEqual(baselineKey, SliceTestCache.ComputeKey(baseInputs with { FilterHash = SliceTestCache.HashFilter("FullyQualifiedName~SliceB", []) }));
    }

    [Fact]
    public void HashFilter_distinguishes_vstest_expressions_and_platform_arguments()
    {
        var none = SliceTestCache.HashFilter(null, []);
        var vstest = SliceTestCache.HashFilter("FullyQualifiedName~Foo.Bar", []);
        var mtp = SliceTestCache.HashFilter(null, ["--filter-method", "*.Bar"]);
        var xunitDirect = SliceTestCache.HashFilter(null, ["-method", "*.Bar"]);

        Assert.NotEqual(none, vstest);
        Assert.NotEqual(none, mtp);
        Assert.NotEqual(mtp, xunitDirect);
    }

    [Fact]
    public void Store_and_TryGet_persist_across_instances_and_respect_noCache_bypass()
    {
        var cache = new SliceTestCache(_stateDir);
        var key = SliceTestCache.ComputeKey(new SliceTestCache.KeyInputs(
            "tests/App.Tests/App.Tests.csproj",
            "net10.0",
            "pe:pdb",
            [("src/Lib/Lib.csproj|net10.0", "lib")],
            [],
            "",
            SliceTestCache.HashFilter(null, [])));

        Assert.Null(cache.TryGet(key));

        var result = new SliceCacheResult(
            key,
            "tests/App.Tests/App.Tests.csproj",
            "App.Tests",
            "net10.0",
            Passed: 3,
            Failed: 1,
            Skipped: 0,
            DurationMs: 12.5,
            CachedAtUtc: new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc),
            Failures: [new SliceTestFailure("App.Tests.BrokenTest", "Expected 2, got 3", ["at App.Tests.BrokenTest()"])]);

        cache.Store(result);

        var expectedFile = Path.Combine(SliceTestCache.DirectoryFor(_stateDir), key + ".json");
        Assert.True(File.Exists(expectedFile));

        // Bypass flag must return null even when warm and persisted on disk.
        Assert.Null(cache.TryGet(key, noCache: true));

        // Cold instance reads the persisted entry from SharedStateDirectory/test-cache/.
        var coldCache = new SliceTestCache(_stateDir);
        var loaded = coldCache.TryGet(key);
        Assert.NotNull(loaded);
        Assert.Equal(3, loaded.Passed);
        Assert.Equal(1, loaded.Failed);
        Assert.Equal(0, loaded.Skipped);
        var failure = Assert.Single(loaded.Failures);
        Assert.Equal("App.Tests.BrokenTest", failure.Name);
        Assert.Equal("Expected 2, got 3", failure.Message);
    }

    [Fact]
    public void TryGet_returns_null_for_corrupted_file_and_Clear_removes_cached_entries()
    {
        var cache = new SliceTestCache(_stateDir);
        var key = SliceTestCache.ComputeKey(new SliceTestCache.KeyInputs(
            "tests/A/A.csproj",
            "net10.0",
            "pe:pdb",
            [],
            [],
            "",
            SliceTestCache.HashFilter(null, [])));

        Directory.CreateDirectory(SliceTestCache.DirectoryFor(_stateDir));
        File.WriteAllText(Path.Combine(SliceTestCache.DirectoryFor(_stateDir), key + ".json"), "{ invalid json");
        Assert.Null(cache.TryGet(key));

        cache.Store(new SliceCacheResult(key, "tests/A/A.csproj", "A", "net10.0", 2, 0, 0, 4.0, DateTime.UtcNow, []));
        Assert.NotNull(cache.TryGet(key));

        cache.Clear();
        Assert.Null(cache.TryGet(key));
        Assert.False(Directory.Exists(SliceTestCache.DirectoryFor(_stateDir)));
    }
}
