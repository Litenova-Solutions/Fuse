using Fuse.Dotnet;
using Fuse.Telemetry;
using Fuse.Testing;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A plan answers a test class from the last round's pass when every input its key covers is unchanged, and runs
///     it for real after an edit in its closure, a resource or project change, or a failure. Whole selections,
///     platform projects and <c>fuse test --all</c> never read the cache.
/// </summary>
public class TestResultCacheTests
{
    [Fact]
    public async Task An_unrelated_edit_after_a_pass_answers_the_class_from_cache()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.Single(first.Runs);
        Assert.Contains("ran 1 test(s) affected by your changes out of 4", first.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("from cache", first.Summary, StringComparison.Ordinal);
        ReportPassed(engine, first, "Lib.Tests.CalcTests.Multiplies");

        // Strict is reached by no test and referenced by no involved project, so the key still holds.
        engine.Repo.Replace("Strict/Thing.cs", "=> 1;", "=> 2;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Empty(second.Runs);
        Assert.Contains("ran 0 test(s) affected by your changes out of 4", second.Summary, StringComparison.Ordinal);
        Assert.Contains("1 result(s) from cache", second.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_in_the_closure_reruns_the_class()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        ReportPassed(engine, first, "Lib.Tests.CalcTests.Multiplies");

        engine.Repo.Replace("Lib/Calc.cs", "b * a;", "b * a + 1;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(second.Runs);
        Assert.Contains("ran 1 test(s) affected by your changes out of 4", second.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("from cache", second.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resource_added_to_the_test_project_reruns_the_class()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        ReportPassed(engine, first, "Lib.Tests.CalcTests.Multiplies");

        engine.Repo.Write("Lib.Tests/notes.txt", "a resource the shadow run would copy");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(second.Runs);
        Assert.DoesNotContain("from cache", second.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_file_change_reruns_the_class()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        ReportPassed(engine, first, "Lib.Tests.CalcTests.Multiplies");

        engine.Repo.Replace("Lib.Tests/Lib.Tests.csproj", "</Project>", "<!-- a comment -->\n</Project>");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(second.Runs);
        Assert.DoesNotContain("from cache", second.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edit_reached_only_through_assembly_setup_reruns_the_class()
    {
        // No test class names the attribute, but the framework constructs it, so what it reaches is in every key.
        using var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib.Tests/Warmup.cs", """
            [assembly: Lib.Tests.Warmup]

            namespace Lib.Tests;

            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class WarmupAttribute : System.Attribute
            {
                public WarmupAttribute() => _ = Lib.Formatter.Format(1);
            }
            """);
        repo.Commit("add assembly setup");
        await using var engine = await InProcessEngine.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        ReportPassed(engine, first, "Lib.Tests.CalcTests.Multiplies");

        repo.Replace("Lib/Formatter.cs", "InvariantCulture);", "InvariantCulture) + \"\";");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(second.Runs);
        Assert.DoesNotContain("from cache", second.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closure_that_finds_types_by_reflection_never_reads_the_cache()
    {
        // Calc scans its assembly, so code no test names can run: the class gets no key and runs every time.
        using var repo = FixtureRepo.CreateStandard();
        repo.Replace("Lib/Calc.cs", "public int Add(int a, int b) => a + b;", "public int Add(int a, int b) => a + b + typeof(Calc).Assembly.GetTypes().Length * 0;");
        repo.Commit("scan the assembly");
        await using var engine = await InProcessEngine.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        ReportPassed(engine, first, "Lib.Tests.CalcTests.Multiplies");

        repo.Replace("Strict/Thing.cs", "=> 1;", "=> 2;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(second.Runs);
        Assert.DoesNotContain("from cache", second.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_class_that_failed_is_rerun()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        var (planId, _) = engine.Planner.WireInfoFor(first);
        var project = engine.Repo.Full("Lib.Tests/Lib.Tests.csproj");
        engine.Planner.ReportResults(planId, [(project, "Lib.Tests.CalcTests.Multiplies", TestVerdict.Passed)]);
        engine.Planner.ReportResults(planId, [(project, "Lib.Tests.CalcTests.Multiplies", TestVerdict.Failed)]);

        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(second.Runs);
        Assert.DoesNotContain("from cache", second.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_all_plan_never_reads_the_cache()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        ReportPassed(engine, first, "Lib.Tests.CalcTests.Multiplies");

        var all = await engine.Planner.PlanAsync(new TestScope.All(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(all.Runs);
        Assert.Contains("ran every test", all.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("from cache", all.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_whole_selection_never_reads_the_cache()
    {
        await using var engine = await InProcessEngine.StartAsync(WithClasslessTests());
        engine.Repo.Replace("App/Report.cs", "\"value=\" + Lib.Formatter.Format(value)", "\"value: \" + Lib.Formatter.Format(value)");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.NotEmpty(plan.Runs);
        Assert.Contains("whole projects where", plan.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("from cache", plan.Summary, StringComparison.Ordinal);

        // A reported pass has no keys to match, so the next plan still runs whole.
        var (planId, _) = engine.Planner.WireInfoFor(plan);
        engine.Planner.ReportResults(planId, [(engine.Repo.Full("App.Tests/App.Tests.csproj"), "App.Tests.ReportTests.Formats", TestVerdict.Passed)]);
        var again = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(again.Runs);
        Assert.DoesNotContain("from cache", again.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_platform_project_never_reads_the_cache()
    {
        using var repo = FixtureRepo.CreateEmpty(FixtureTemplate.PlatformRepo("xunit-v3", optIn: true));
        FixtureRepo.Run(repo.Root.Path, "dotnet", "restore", FixtureTemplate.PlatformSolutionFile, "-nologo", "-v:q");
        await using var engine = await InProcessEngine.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "=> a + b;", "=> a + b + 0;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(plan.Runs);
        Assert.DoesNotContain("from cache", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_store_survives_a_reload_and_ignores_a_foreign_build_or_a_corrupt_file()
    {
        using var repo = FixtureRepo.CreateStandard();
        var stored = new CachedPass("Lib.Tests.csproj", "Lib.Tests.CalcTests", "key", ["Lib.Tests.CalcTests.Multiplies"], DateTime.UtcNow);
        new TestResultCache(repo.Root.StateDirectory, _ => { }, "build-a").Store(stored);

        var reloaded = new TestResultCache(repo.Root.StateDirectory, _ => { }, "build-a");
        Assert.True(reloaded.TryGet("Lib.Tests.csproj", "Lib.Tests.CalcTests", out var pass));
        Assert.Equal("key", pass.Key);
        Assert.Equal("Lib.Tests.CalcTests.Multiplies", Assert.Single(pass.Tests));

        var foreign = new TestResultCache(repo.Root.StateDirectory, _ => { }, "build-b");
        Assert.False(foreign.TryGet("Lib.Tests.csproj", "Lib.Tests.CalcTests", out _));

        File.WriteAllText(Path.Combine(repo.Root.StateDirectory, "test-result-cache.json"), "{corrupt");
        var logged = new List<string>();
        Assert.False(new TestResultCache(repo.Root.StateDirectory, logged.Add, "build-a").TryGet("Lib.Tests.csproj", "Lib.Tests.CalcTests", out _));
        Assert.Single(logged);
    }

    /// <summary>Reports the named tests of Lib.Tests as passed for <paramref name="plan"/>, the way a shadow run's client would.</summary>
    private static void ReportPassed(InProcessEngine engine, TestPlanResult plan, params string[] tests)
    {
        var (planId, _) = engine.Planner.WireInfoFor(plan);
        Assert.NotEqual("", planId);
        var project = engine.Repo.Full("Lib.Tests/Lib.Tests.csproj");
        engine.Planner.ReportResults(planId, [.. tests.Select(t => (project, t, TestVerdict.Passed))]);
    }

    /// <summary>The standard repository whose entry point calls <c>Report.Line</c>, with the test class replaced by top-level statements, so App.Tests runs whole.</summary>
    private static FixtureRepo WithClasslessTests()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Replace("App/Program.cs", "calc.Add(1, 2)", "calc.Add(1, 2) + App.Report.Line(1).Length");
        repo.Write("App.Tests/ReportTests.cs", """
            System.Console.WriteLine(App.Report.Line(5));
            """);
        repo.Commit("replace the test class with top-level statements");
        return repo;
    }
}
