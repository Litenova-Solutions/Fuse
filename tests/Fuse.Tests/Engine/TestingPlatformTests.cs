using Fuse.Engine;
using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Fixture repositories on Microsoft.Testing.Platform for xUnit v3, MSTest, NUnit and TUnit, with and without the
///     <c>global.json</c> opt-in: what the graph reports, what the planner filters each run to, and what running the
///     plan answers (#73, #57). Without the opt-in the run builds the project and runs its test application directly.
/// </summary>
public class TestingPlatformTests
{
    [Theory]
    [InlineData("xunit-v3", "XUnit")]
    [InlineData("mstest", "MSTest")]
    [InlineData("nunit", "NUnit")]
    [InlineData("tunit", "TUnit")]
    public async Task The_platform_and_its_framework_come_from_the_project(string framework, string expected)
    {
        foreach (var optIn in new[] { true, false })
        {
            await using var engine = await InProcessEngine.StartAsync(CreatePlatformRepo(framework, optIn));
            var node = engine.Workspace.Graph.Projects.Single(p => p.Name == "Tests");

            Assert.True(node.IsTest);
            Assert.True(node.UsesTestingPlatform);
            Assert.Equal(expected, node.Framework.ToString());
            var target = Assert.Single(node.TargetPaths);
            Assert.EndsWith("Tests.dll", target, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("xunit-v3", true)]
    [InlineData("xunit-v3", false)]
    [InlineData("mstest", true)]
    [InlineData("mstest", false)]
    [InlineData("nunit", true)]
    [InlineData("nunit", false)]
    [InlineData("tunit", true)]
    [InlineData("tunit", false)]
    public async Task A_change_in_Lib_plans_the_platform_project_filtered_to_the_affected_test(string framework, bool optIn)
    {
        await using var engine = await InProcessEngine.StartAsync(CreatePlatformRepo(framework, optIn));
        engine.Repo.Replace("Lib/Calc.cs", "=> a + b;", "=> a + b + 0;");
        await engine.SyncAsync("Lib/Calc.cs");
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        var run = Assert.Single(plan.Runs);
        Assert.Equal("Tests", run.Name);
        Assert.True(run.UsesTestingPlatform);
        Assert.IsType<RunMode.Build>(run.Mode);
        Assert.Equal(1, plan.SelectedTests);
        Assert.Equal(2, plan.TotalTests);
        switch (framework)
        {
            case "mstest":
            case "nunit":
                // Both accept the VSTest filter on the platform, so it travels as a runsettings filter does.
                Assert.Equal("FullyQualifiedName~Platform.Tests.CalcTests.Adds", run.Filter);
                Assert.Empty(run.FilterArguments);
                break;
            case "xunit-v3":
                Assert.Null(run.Filter);
                Assert.Equal([optIn ? "--filter-method" : "-method", "Platform.Tests.CalcTests.Adds"], run.FilterArguments);
                break;
            case "tunit":
                Assert.Null(run.Filter);
                Assert.Equal(["--treenode-filter", "/*/Platform.Tests/CalcTests/Adds"], run.FilterArguments);
                break;
        }
    }

    [Theory]
    [InlineData("xunit-v3")]
    [InlineData("mstest")]
    [InlineData("nunit")]
    [InlineData("tunit")]
    public async Task A_platform_run_with_the_opt_in_passes_whole(string framework)
    {
        using var repo = CreatePlatformRepo(framework, optIn: true);
        var run = new TestRun(repo.Full("Tests/Tests.csproj"), "Tests", new TestRunMode.Build(), null, true, null, [], []);

        var result = await TestOperation.RunPlanAsync(repo.Root, new TestPlan([run], 2, 2, "ran 2 test(s)"), Environment.TickCount64, TestContext.Current.CancellationToken);

        Assert.Equal(Outcome.Clean, result.Outcome);
        Assert.Contains("0 failed, 2 passed", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xunit-v3", true)]
    [InlineData("xunit-v3", false)]
    [InlineData("mstest", true)]
    [InlineData("mstest", false)]
    [InlineData("nunit", true)]
    [InlineData("nunit", false)]
    [InlineData("tunit", true)]
    [InlineData("tunit", false)]
    public async Task A_filtered_platform_run_runs_only_the_affected_test(string framework, bool optIn)
    {
        using var repo = CreatePlatformRepo(framework, optIn);
        await using var engine = await InProcessEngine.StartAsync(repo);
        repo.Replace("Lib/Calc.cs", "=> a + b;", "=> a + b + 0;");
        await engine.SyncAsync("Lib/Calc.cs");
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        var result = await TestOperation.RunPlanAsync(repo.Root, ResponseMapper.Plan(plan), Environment.TickCount64, TestContext.Current.CancellationToken);

        Assert.Equal(Outcome.Clean, result.Outcome);
        Assert.Contains("0 failed, 1 passed", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xunit-v3")]
    [InlineData("mstest")]
    [InlineData("nunit")]
    [InlineData("tunit")]
    public async Task A_filtered_platform_run_without_the_opt_in_reports_the_failing_test(string framework)
    {
        using var repo = CreatePlatformRepo(framework, optIn: false);
        await using var engine = await InProcessEngine.StartAsync(repo);
        // Adds now fails; Multiplies still passes, and only Adds is affected.
        repo.Replace("Lib/Calc.cs", "=> a + b;", "=> a + b + 1;");
        await engine.SyncAsync("Lib/Calc.cs");
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        var result = await TestOperation.RunPlanAsync(repo.Root, ResponseMapper.Plan(plan), Environment.TickCount64, TestContext.Current.CancellationToken);

        Assert.Equal(Outcome.ProblemsFound, result.Outcome);
        // TUnit names a failed test by its method alone; the others by its fully qualified name.
        Assert.Contains(framework == "tunit" ? "FAILED Adds" : "CalcTests.Adds", result.Text, StringComparison.Ordinal);
        Assert.Contains("1 failed", result.Text, StringComparison.Ordinal);
    }

    /// <summary>Copies the platform template for <paramref name="framework"/> and restores it, so MSBuild resolves its runner targets.</summary>
    private static FixtureRepo CreatePlatformRepo(string framework, bool optIn)
    {
        var repo = FixtureRepo.CreateEmpty(FixtureTemplate.PlatformRepo(framework, optIn));
        FixtureRepo.Run(repo.Root.Path, "dotnet", "restore", FixtureTemplate.PlatformSolutionFile, "-nologo", "-v:q");
        return repo;
    }
}
