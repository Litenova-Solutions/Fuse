using System.Text;
using Fuse.Engine;
using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The wired test-run paths: a stored slice serves the next plan without executing, a passing cached verdict
///     reuses its counts on the client while a failing one runs fresh, and an incoming platform test map is
///     ingested and removed by the next plan.
/// </summary>
public class TestRunPathTests
{
    [Fact]
    public async Task Prepare_serves_a_stored_slice_without_executing()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.SyncAsync("Lib/Calc.cs");
        var cancellationToken = TestContext.Current.CancellationToken;

        var pending = await engine.Planner.SelectAsync(new TestScope.Affected(), PhaseTimes.None, cancellationToken);
        var key = Assert.Single((await engine.Planner.PrepareAsync(pending, PhaseTimes.None, cancellationToken)).Runs).SliceKey;
        Assert.NotNull(key);
        engine.Planner.SliceCache.Store(new SliceCacheResult(key, "Lib.Tests/Lib.Tests.csproj", "Lib.Tests", "", Passed: 3, Failed: 0, Skipped: 0, DurationMs: 4, CachedAtUtc: DateTime.UtcNow, Failures: []));

        var served = await engine.Planner.PrepareAsync(
            await engine.Planner.SelectAsync(new TestScope.Affected(), PhaseTimes.None, cancellationToken),
            PhaseTimes.None,
            cancellationToken);
        var cached = Assert.Single(served.Runs).CachedResult;

        Assert.NotNull(cached);
        Assert.Equal((3, 0, 0), (cached.Passed, cached.Failed, cached.Skipped));
    }

    [Fact]
    public void A_cached_passing_verdict_reuses_its_counts_without_running()
    {
        var root = FixtureRepo.CheckoutRoot;
        var run = new PlannedRun(
            root.PathOf("Lib.Tests/Lib.Tests.csproj"),
            "Lib.Tests",
            new RunMode.Shadow("C:/state/shadow/Lib.Tests/Lib.Tests.dll"),
            null,
            false,
            null,
            [],
            [])
        {
            SliceKey = "key",
            CachedResult = new SliceCacheResult("key", "Lib.Tests/Lib.Tests.csproj", "Lib.Tests", "", Passed: 5, Failed: 0, Skipped: 1, DurationMs: 4, CachedAtUtc: DateTime.UtcNow, Failures: []),
        };

        var outcome = TestOperation.CachedPassOutcome(Assert.Single(ResponseMapper.Plan(new TestPlanResult([run], 6, 6, "summary")).Runs));

        Assert.NotNull(outcome);
        Assert.Equal((5, 0, 1), (outcome.Passed, outcome.Failed, outcome.Skipped));
        Assert.Empty(outcome.Failures);
    }

    [Fact]
    public void A_cached_failing_verdict_an_uncached_run_and_a_malformed_reason_run_fresh()
    {
        var shadow = new TestRunMode.Shadow("C:/state/shadow/Lib.Tests/Lib.Tests.dll");
        var failed = new TestRun("C:/repo/Lib.Tests/Lib.Tests.csproj", "Lib.Tests", shadow, null, false, null, [], [])
        {
            FromCache = true,
            CacheReason = "slice cache: 3 passed, 2 failed, 0 skipped",
        };
        var fresh = new TestRun("C:/repo/Lib.Tests/Lib.Tests.csproj", "Lib.Tests", shadow, null, false, null, [], []);
        var malformed = fresh with { FromCache = true, CacheReason = "slice cache: someday" };

        Assert.Null(TestOperation.CachedPassOutcome(failed));
        Assert.Null(TestOperation.CachedPassOutcome(fresh));
        Assert.Null(TestOperation.CachedPassOutcome(malformed));
    }

    [Fact]
    public async Task An_incoming_test_map_is_ingested_and_removed_by_the_next_plan()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var incoming = Path.Combine(DynamicTestMap.IncomingDirectoryFor(engine.Repo.Root.SharedStateDirectory), Convert.ToHexStringLower(Encoding.UTF8.GetBytes("Lib.Tests/Lib.Tests.csproj")));
        Directory.CreateDirectory(incoming);
        File.WriteAllText(Path.Combine(incoming, "run.testmap.json"), """{"methods": {"Lib.Calc.Mul": ["Lib.Tests.CalcTests.Multiplies"]}}""");
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.SyncAsync("Lib/Calc.cs");

        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(plan.Runs);
        Assert.True(engine.Planner.DynamicMap.HasAnyMapping);
        Assert.False(Directory.Exists(incoming));
    }

    [Fact]
    public async Task An_unparsable_incoming_map_is_dropped_without_failing_the_plan()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var incoming = DynamicTestMap.IncomingDirectoryFor(engine.Repo.Root.SharedStateDirectory);
        var garbage = Path.Combine(incoming, Convert.ToHexStringLower(Encoding.UTF8.GetBytes("Lib.Tests/Lib.Tests.csproj")));
        Directory.CreateDirectory(garbage);
        File.WriteAllText(Path.Combine(garbage, "bad.testmap.json"), "not json {{{");
        var unnamed = Path.Combine(incoming, "zz-not-hex");
        Directory.CreateDirectory(unnamed);
        File.WriteAllText(Path.Combine(unnamed, "run.testmap.json"), """{"methods": {"Lib.Calc.Mul": ["Lib.Tests.CalcTests.Multiplies"]}}""");
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 1;");
        await engine.SyncAsync("Lib/Calc.cs");

        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(plan.Runs);
        Assert.False(engine.Planner.DynamicMap.HasAnyMapping);
        Assert.Empty(Directory.EnumerateFileSystemEntries(incoming));
    }
}
