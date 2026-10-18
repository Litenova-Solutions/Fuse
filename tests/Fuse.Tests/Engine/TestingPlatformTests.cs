using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     Fixture repositories on Microsoft.Testing.Platform for xUnit v3, MSTest, NUnit and TUnit, with and without the
///     <c>global.json</c> opt-in: what the graph reports and what <c>fuse test</c> answers for each (#73). A project
///     with the opt-in runs whole because each framework spells its filter differently; #57 adds their filters.
/// </summary>
public class TestingPlatformTests
{
    [Theory]
    [InlineData("xunit-v3", true)]
    [InlineData("xunit-v3", false)]
    [InlineData("mstest", true)]
    [InlineData("mstest", false)]
    [InlineData("nunit", true)]
    [InlineData("nunit", false)]
    [InlineData("tunit", true)]
    [InlineData("tunit", false)]
    public async Task UsesTestingPlatform_matches_the_opt_in(string framework, bool optIn)
    {
        await using var engine = await InProcessEngine.StartAsync(CreatePlatformRepo(framework, optIn));
        var node = engine.Workspace.Graph.Projects.Single(p => p.Name == "Tests");

        Assert.True(node.IsTest);
        Assert.Equal(optIn, node.UsesTestingPlatform);
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
    public async Task A_change_in_Lib_plans_the_platform_project(string framework, bool optIn)
    {
        await using var engine = await InProcessEngine.StartAsync(CreatePlatformRepo(framework, optIn));
        engine.Repo.Replace("Lib/Calc.cs", "=> a + b;", "=> a + b + 0;");
        // The file watcher reports the edit a moment later.
        await Task.Delay(400, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PlanAsync(new TestScope.Affected(), PhaseTimes.None, TestContext.Current.CancellationToken);

        var run = Assert.Single(plan.Runs);
        Assert.Equal("Tests", run.Name);
        Assert.Equal(optIn, run.UsesTestingPlatform);
        Assert.IsType<RunMode.Build>(run.Mode);
        if (optIn)
        {
            // Each framework spells its filter differently, so the project runs whole (#57).
            Assert.Null(run.Filter);
            Assert.Equal(2, plan.SelectedTests);
        }
        else
        {
            Assert.Equal("FullyQualifiedName~Platform.Tests.CalcTests.Adds", run.Filter);
            Assert.Equal(1, plan.SelectedTests);
        }

        Assert.Equal(2, plan.TotalTests);
    }

    [Theory]
    [InlineData("xunit-v3")]
    [InlineData("mstest")]
    [InlineData("nunit")]
    [InlineData("tunit")]
    public async Task A_platform_run_with_the_opt_in_passes_whole(string framework)
    {
        using var repo = CreatePlatformRepo(framework, optIn: true);
        var run = new TestRun(repo.Full("Tests/Tests.csproj"), "Tests", new TestRunMode.Build(), null, true, null);

        var result = await TestOperation.RunPlanAsync(repo.Root, new TestPlan([run], 2, 2, "ran 2 test(s)"), Environment.TickCount64, TestContext.Current.CancellationToken);

        Assert.Equal(Outcome.Clean, result.Outcome);
        Assert.Contains("0 failed, 2 passed", result.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mstest")]
    [InlineData("nunit")]
    [InlineData("tunit")]
    public async Task A_platform_shape_without_the_opt_in_reports_problems(string framework)
    {
        using var repo = CreatePlatformRepo(framework, optIn: false);
        // The planner treats the project as VSTest and filters to the affected test, but without the opt-in the run
        // cannot go through VSTest: the MSTest targets refuse the VSTest target on .NET 10, and the NUnit and TUnit
        // shapes name no Test SDK, so the run reports problems (#73).
        var run = new TestRun(repo.Full("Tests/Tests.csproj"), "Tests", new TestRunMode.Build(), "FullyQualifiedName~Platform.Tests.CalcTests.Adds", false, null);

        var result = await TestOperation.RunPlanAsync(repo.Root, new TestPlan([run], 1, 2, "ran 1 test(s)"), Environment.TickCount64, TestContext.Current.CancellationToken);

        Assert.Equal(Outcome.ProblemsFound, result.Outcome);
    }

    [Fact]
    public async Task The_xunit_v3_shape_without_the_opt_in_answers_a_pass_having_run_nothing()
    {
        using var repo = CreatePlatformRepo("xunit-v3", optIn: false);
        // Without the opt-in, dotnet test finds no VSTest target in an xUnit v3 project (it names no Test SDK) and
        // exits 0 having run nothing, so Fuse answers a pass for tests that never ran. #57 runs such a project on
        // the platform instead; until then this records the false pass (#73).
        var run = new TestRun(repo.Full("Tests/Tests.csproj"), "Tests", new TestRunMode.Build(), "FullyQualifiedName~Platform.Tests.CalcTests.Adds", false, null);

        var result = await TestOperation.RunPlanAsync(repo.Root, new TestPlan([run], 1, 2, "ran 1 test(s)"), Environment.TickCount64, TestContext.Current.CancellationToken);

        Assert.Equal(Outcome.Clean, result.Outcome);
    }

    /// <summary>Copies the platform template for <paramref name="framework"/> and restores it, so MSBuild resolves its runner targets.</summary>
    private static FixtureRepo CreatePlatformRepo(string framework, bool optIn)
    {
        var repo = FixtureRepo.CreateEmpty(FixtureTemplate.PlatformRepo(framework, optIn));
        FixtureRepo.Run(repo.Root.Path, "dotnet", "restore", FixtureTemplate.PlatformSolutionFile, "-nologo", "-v:q");
        return repo;
    }
}
