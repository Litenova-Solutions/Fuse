using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     An edit that compiles to the same code as HEAD's selects no tests: a local rename or an equivalent rewrite seeds
///     neither walk, and a plan that selects nothing for that reason says so. A body edit that does change the code still
///     selects its tests.
/// </summary>
public class SameCodeSelectionTests
{
    private static async Task<Dictionary<string, TestSelection>> SelectAsync(InProcessEngine engine, params string[] files)
    {
        await engine.Workspace.SyncAsync(files.Select(engine.Repo.PathOf), TestContext.Current.CancellationToken);
        var result = await engine.Selector.SelectAsync(files.Select(engine.Repo.PathOf).ToList(), TestContext.Current.CancellationToken);
        return result.Selections.ToDictionary(kv => Path.GetFileNameWithoutExtension(kv.Key.Absolute), kv => kv.Value);
    }

    [Fact]
    public async Task Renamed_local_selects_no_tests_and_says_so()
    {
        await using var engine = await InProcessEngine.StartAsync(WithWorker());
        engine.Repo.Replace("Lib/Worker.cs", "doubled", "result");
        Assert.Empty(await SelectAsync(engine, "Lib/Worker.cs"));

        await Task.Delay(400, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Empty(plan.Runs);
        Assert.Equal("no test is affected by the changes (out of 5); the changes compile to the same code as HEAD; fuse test --all runs everything", plan.Summary);
        Assert.Contains(engine.Log, line => line.Contains("compile to the same code as HEAD", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Equivalent_var_rewrite_selects_no_tests()
    {
        await using var engine = await InProcessEngine.StartAsync(WithWorker());
        engine.Repo.Replace("Lib/Worker.cs", "int doubled = input * 2;", "var doubled = input * 2;");
        Assert.Empty(await SelectAsync(engine, "Lib/Worker.cs"));
    }

    [Fact]
    public async Task Changed_computation_still_selects_its_tests()
    {
        await using var engine = await InProcessEngine.StartAsync(WithWorker());
        engine.Repo.Replace("Lib/Worker.cs", "input * 2", "input * 3");
        var selection = await SelectAsync(engine, "Lib/Worker.cs");
        Assert.Equal(["Lib.Tests.CalcTests.Computes"], Assert.IsType<TestSelection.Methods>(selection["Lib.Tests"]).Patterns);
        Assert.DoesNotContain("App.Tests", selection.Keys);
    }

    [Fact]
    public async Task Comment_only_edit_says_the_code_is_the_same()
    {
        await using var engine = await InProcessEngine.StartAsync(WithWorker());
        engine.Repo.Replace("Lib/Worker.cs", "int doubled = input * 2;", "int doubled = input * 2; // doubled");
        Assert.Empty(await SelectAsync(engine, "Lib/Worker.cs"));

        await Task.Delay(400, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Empty(plan.Runs);
        Assert.Equal("no test is affected by the changes (out of 5); the changes compile to the same code as HEAD; fuse test --all runs everything", plan.Summary);
    }

    [Fact]
    public async Task Renamed_local_plus_error_still_reports_the_error()
    {
        await using var engine = await InProcessEngine.StartAsync(WithWorker());
        engine.Repo.Replace("Lib/Worker.cs", "doubled", "result");
        engine.Repo.Replace("Lib/Worker.cs", "result + 1", "result + unknownName");
        var report = await engine.CheckAsync("Lib/Worker.cs");

        var error = Assert.Single(report.Errors).Error;
        Assert.Equal("Lib/Worker.cs", error.Path);
        Assert.Equal("CS0103", error.Id);
        Assert.Equal(8, error.Line);
        Assert.Empty(report.DeclarationsChangedIn);
        Assert.Equal(0, report.DependentProjectsChecked);
        Assert.Equal(1, report.FilesChecked);
    }

    /// <summary>The standard repository with a committed <c>Lib/Worker.cs</c> whose method a test reaches.</summary>
    private static FixtureRepo WithWorker()
    {
        var repo = FixtureRepo.CreateStandard();
        repo.Write("Lib/Worker.cs", """
            namespace Lib;

            public class Worker
            {
                public int Compute(int input)
                {
                    int doubled = input * 2;
                    return doubled + 1;
                }
            }
            """);
        repo.Replace("Lib.Tests/CalcTests.cs", "    private static Calc NewCalc() => new();", """
                [Fact]
                public void Computes() => Assert.Equal(5, new Worker().Compute(2));

                private static Calc NewCalc() => new();
            """);
        repo.Commit("add a worker with a named local");
        return repo;
    }
}
