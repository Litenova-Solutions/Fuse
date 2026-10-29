using Fuse.Failures;
using Fuse.Telemetry;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

/// <summary>
///     A plan limited to the paths the command named: only those test projects run, the summary says which, a path
///     naming no test project is refused instead of passing, and a named configuration or framework leaves no shadow
///     run in the plan (#69).
/// </summary>
public class NamedScopeTests
{
    [Fact]
    public async Task A_directory_limits_the_plan_to_its_test_projects()
    {
        await using var engine = await InProcessEngine.StartAsync();
        // The greeting reaches Lib.Tests and the report line reaches App.Tests; the plan names only App.Tests.
        engine.Repo.Replace("App/Report.cs", "\"value=\" + Lib.Formatter.Format(value)", "\"value: \" + Lib.Formatter.Format(value)");
        engine.Repo.Replace("Lib/Greeting.cs", "\"Hello \" + name", "\"Hello, \" + name");
        await engine.SyncAsync("App/Report.cs", "Lib/Greeting.cs");
        var scope = new TestScope.Affected { NamedPaths = [engine.Repo.Full("App.Tests")] };

        var plan = await engine.Planner.PlanAsync(scope, PhaseTimes.None, TestContext.Current.CancellationToken);

        var run = Assert.Single(plan.Runs);
        Assert.Equal("App.Tests", run.Name);
        Assert.Equal(1, plan.SelectedTests);
        Assert.Equal(1, plan.TotalTests);
        Assert.Contains("; in App.Tests", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_project_file_and_the_solution_name_their_test_projects()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Greeting.cs", "\"Hello \" + name", "\"Hello, \" + name");
        await engine.SyncAsync("Lib/Greeting.cs");

        engine.Repo.Replace("App/Report.cs", "\"value=\" + Lib.Formatter.Format(value)", "\"value: \" + Lib.Formatter.Format(value)");
        await engine.SyncAsync("App/Report.cs");

        var one = await engine.Planner.PlanAsync(
            new TestScope.Affected { NamedPaths = [engine.Repo.Full("Lib.Tests/Lib.Tests.csproj")] },
            PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.Equal("Lib.Tests", Assert.Single(one.Runs).Name);
        Assert.Equal(3, one.TotalTests);

        var both = await engine.Planner.PlanAsync(
            new TestScope.Affected { NamedPaths = [engine.Repo.Full(FixtureTemplate.SolutionFile)] },
            PhaseTimes.None, TestContext.Current.CancellationToken);
        Assert.Equal(["App.Tests", "Lib.Tests"], both.Runs.Select(r => r.Name).Order().ToArray());
        Assert.Equal(4, both.TotalTests);
    }

    [Fact]
    public async Task A_path_naming_no_test_project_is_refused()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var scope = new TestScope.Affected { NamedPaths = [engine.Repo.Full("Lib")] };

        var refused = await Assert.ThrowsAsync<FuseException>(() =>
            engine.Planner.PlanAsync(scope, PhaseTimes.None, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.InvalidPath, refused.Code);
        Assert.Contains("none of the paths given", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_named_configuration_builds_every_run()
    {
        await using var engine = await InProcessEngine.StartAsync();
        engine.Repo.Replace("Lib/Calc.cs", "a * b;", "a * b + 0;");
        await engine.SyncAsync("Lib/Calc.cs");
        var scope = new TestScope.Affected { BuildOnly = true };

        var plan = await engine.Planner.PlanAsync(scope, PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.All(plan.Runs, run => Assert.IsType<RunMode.Build>(run.Mode));
    }
}
