using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A test class whose forward closure reads the clock or a file outside its project never answers from the cache,
///     even when the class's own files name no marker. The guard scans every file the class reaches, not only its own.
/// </summary>
public class TransitiveCacheGuardTests
{
    [Fact]
    public async Task A_clock_read_in_a_same_project_helper_reruns_the_class()
    {
        await using var engine = await InProcessEngine.StartAsync(WithClockHelper());
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.Single(first.Runs);
        ReportPassed(engine, first, "Lib.Tests.CalcTests.Multiplies");

        engine.Repo.Replace("Strict/Thing.cs", "=> 1;", "=> 2;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(second.Runs);
        Assert.DoesNotContain("from cache", second.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_outside_read_in_a_referenced_library_reruns_the_class()
    {
        await using var engine = await InProcessEngine.StartAsync(WithOutsideHelper());
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "b * a;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var first = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.Single(first.Runs);
        ReportPassed(engine, first, "Lib.Tests.CalcTests.Multiplies");

        engine.Repo.Replace("Strict/Thing.cs", "=> 1;", "=> 2;");
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var second = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.NotEmpty(second.Runs);
        Assert.DoesNotContain("from cache", second.Summary, StringComparison.Ordinal);
    }

    /// <summary>The standard repository with a clock-reading helper in <c>Lib.Tests</c> that <c>CalcTests</c> calls.</summary>
    private static FixtureRepo WithClockHelper()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib.Tests/Clock.cs", """
            namespace Lib.Tests;

            public static class Clock
            {
                public static string Today() => System.DateTime.Now.ToString();
            }
            """);
        repo.Replace(
            "Lib.Tests/CalcTests.cs",
            "public void Multiplies() => Assert.Equal(6, NewCalc().Mul(2, 3));",
            "public void Multiplies()\n            {\n                Clock.Today();\n                Assert.Equal(6, NewCalc().Mul(2, 3));\n            }");
        repo.Commit("add a clock-reading helper to Lib.Tests");
        return repo;
    }

    /// <summary>The standard repository with an outside-reading helper in <c>Lib</c> that <c>CalcTests</c> calls.</summary>
    private static FixtureRepo WithOutsideHelper()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib/DataFile.cs", """
            namespace Lib;

            public static class DataFile
            {
                public static string Read() => System.IO.File.ReadAllText("../data.txt");
            }
            """);
        repo.Replace(
            "Lib.Tests/CalcTests.cs",
            "public void Multiplies() => Assert.Equal(6, NewCalc().Mul(2, 3));",
            "public void Multiplies()\n            {\n                Lib.DataFile.Read();\n                Assert.Equal(6, NewCalc().Mul(2, 3));\n            }");
        repo.Commit("add an outside-reading helper to Lib");
        return repo;
    }

    /// <summary>Reports the named tests of Lib.Tests as passed for <paramref name="plan"/>, the way a shadow run's client would.</summary>
    private static void ReportPassed(InProcessEngine engine, TestPlanResult plan, params string[] tests)
    {
        var (planId, _) = engine.Planner.WireInfoFor(plan);
        Assert.NotEqual("", planId);
        var project = engine.Repo.Full("Lib.Tests/Lib.Tests.csproj");
        engine.Planner.ReportResults(planId, [.. tests.Select(t => (project, t, Fuse.Dotnet.TestVerdict.Passed))]);
    }
}
