using System.Text;
using Fuse.Dotnet;
using Fuse.Engine;
using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     The wired test-run paths: a finished shadow run fills the cache for the next plan, a stored slice serves the
///     next plan without executing, a passing cached verdict reuses its counts on the client while a failing one
///     runs fresh, and an incoming platform test map is ingested and removed by the next plan.
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
    public async Task A_finished_shadow_run_fills_the_cache_for_the_next_plan()
    {
        using var repo = FixtureRepo.CreateStandard();
        await using var engine = await InProcessRequestRouter.StartAsync(repo);
        // A commutative reordering: the declaration changes, so the plan selects its tests, and every test still passes.
        repo.Replace("Lib/Calc.cs", "a * b", "b * a");
        // The check syncs the edit by name, so every plan below sees it without waiting for the watcher.
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true)));
        FixtureRepo.Run(repo.Root.Path, "dotnet", "build", "Lib.Tests/Lib.Tests.csproj", "--no-restore", "-nologo", "-v:q");

        var first = Assert.IsType<EngineResponse.PlanAnswered>(await engine.SendAsync(new EngineRequest.PlanAffectedTests())).Plan;
        var run = Assert.Single(first.Runs);
        Assert.False(run.FromCache);
        var shadow = Assert.IsType<TestRunMode.Shadow>(run.Mode);

        var started = Environment.TickCount64;
        var outcome = await RunShadowAsync(repo, shadow.Assembly, run.Filter);
        Assert.True(outcome.Total > 0);
        Assert.Equal(0, outcome.Failed);
        var store = TestOperation.SliceStoreFor(run, outcome, Environment.TickCount64 - started, noCache: false);
        Assert.NotNull(store);
        // Through the pipe records, the way the client sends it.
        var sent = Assert.IsType<EngineRequest.StoreSliceResult>(ProtocolJson.ReadRequest(ProtocolJson.Serialize(store)));
        Assert.IsType<EngineResponse.Acknowledged>(await engine.SendAsync(sent));

        var second = Assert.IsType<EngineResponse.PlanAnswered>(await engine.SendAsync(new EngineRequest.PlanAffectedTests())).Plan;
        var cached = Assert.Single(second.Runs);
        Assert.True(cached.FromCache);
        Assert.NotNull(cached.CacheReason);
        // A cached passing verdict reuses its counts, so the client starts no test process.
        Assert.NotNull(TestOperation.CachedPassOutcome(cached));

        repo.Replace("Lib/Calc.cs", "a + b", "a + b + 0");
        Assert.IsType<EngineResponse.CheckAnswered>(await engine.SendAsync(new EngineRequest.CheckFiles([repo.Full("Lib/Calc.cs")], WaitForLoad: true)));
        var third = Assert.IsType<EngineResponse.PlanAnswered>(await engine.SendAsync(new EngineRequest.PlanAffectedTests())).Plan;
        Assert.False(Assert.Single(third.Runs).FromCache);
    }

    [Fact]
    public void A_slice_verdict_is_stored_only_for_a_complete_run_of_its_slice()
    {
        var shadow = new TestRunMode.Shadow("C:/state/shadow/Lib.Tests/Lib.Tests.dll");
        TestRun Run(string? key) => new("C:/repo/Lib.Tests/Lib.Tests.csproj", "Lib.Tests", shadow, null, false, null, [], []) { SliceKey = key };
        var passed = new TrxResults(3, 0, 0, []);
        var failed = new TrxResults(2, 1, 0, [new TestFailure("Lib.Tests.CalcTests.Multiplies", "boom", [])]);

        Assert.NotNull(TestOperation.SliceStoreFor(Run("key"), passed, 4, noCache: false));
        // A failed verdict is stored too; the client runs a cached failure fresh anyway.
        var storedFailure = TestOperation.SliceStoreFor(Run("key"), failed, 4, noCache: false);
        Assert.NotNull(storedFailure);
        Assert.Equal(1, storedFailure.Failed);
        Assert.Null(TestOperation.SliceStoreFor(Run("key"), passed, 4, noCache: true));
        Assert.Null(TestOperation.SliceStoreFor(Run(null), passed, 4, noCache: false));
        Assert.Null(TestOperation.SliceStoreFor(Run(" "), passed, 4, noCache: false));
        Assert.Null(TestOperation.SliceStoreFor(Run("key"), null, 4, noCache: false));
        Assert.Null(TestOperation.SliceStoreFor(Run("key"), TrxResults.Empty, 4, noCache: false));
        var early = new TrxResults(0, 2, 0, [new TestFailure("Lib.Tests.CalcTests.Multiplies", TestOperation.StoppedEarlyMessage, [])]);
        Assert.Null(TestOperation.SliceStoreFor(Run("key"), early, 4, noCache: false));
    }

    /// <summary>Runs a shadow run's test assembly with <c>dotnet test</c>, the way <c>fuse test</c> does, and reads its results.</summary>
    private static async Task<TrxResults> RunShadowAsync(FixtureRepo repo, string assembly, string? filter)
    {
        var results = FixtureRepo.NewDirectory();
        try
        {
            string[] arguments = ["test", assembly, .. filter is null ? Array.Empty<string>() : ["--filter", filter], "--logger", "trx", "--results-directory", results];
            var run = await ProcessRunner.RunAsync("dotnet", arguments, repo.Root.Path, TestContext.Current.CancellationToken);
            return TrxReader.ReadDirectory(results, repo.Root.Path) ?? throw new InvalidOperationException($"dotnet test wrote no results (exit code {run.ExitCode}):\n{run.Output}");
        }
        finally
        {
            Directory.Delete(results, recursive: true);
        }
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
