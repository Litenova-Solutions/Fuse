using Fuse.Telemetry;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Engine;

public class FailedPlanTests
{
    [Fact]
    public async Task A_rerun_filters_each_project_to_its_recorded_failures()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var project = engine.Repo.PathOf("Lib.Tests/Lib.Tests.csproj");
        var pending = await engine.Planner.SelectFailedAsync(
            new Dictionary<Fuse.Paths.RepoPath, string[]> { [project] = ["Lib.Tests.CalcTests.Multiplies"] },
            [], PhaseTimes.None, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PrepareAsync(pending, PhaseTimes.None, TestContext.Current.CancellationToken);

        var run = Assert.Single(plan.Runs);
        Assert.Equal("Lib.Tests", run.Name);
        Assert.Equal("FullyQualifiedName~Lib.Tests.CalcTests.Multiplies", run.Filter);
        Assert.Equal("reran 1 test(s) that failed last time in 1 test project(s)", plan.Summary);
    }

    [Fact]
    public async Task Recorded_projects_that_are_gone_or_hold_no_tests_are_left_out_and_named()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var pending = await engine.Planner.SelectFailedAsync(
            new Dictionary<Fuse.Paths.RepoPath, string[]>
            {
                [engine.Repo.PathOf("Gone/Gone.csproj")] = ["Gone.Tests.Run"],
                [engine.Repo.PathOf("Lib/Lib.csproj")] = ["Lib.Tests.Run"],
            },
            ["/nowhere/Else.Tests.csproj"], PhaseTimes.None, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PrepareAsync(pending, PhaseTimes.None, TestContext.Current.CancellationToken);

        Assert.Empty(plan.Runs);
        Assert.Equal(
            "reran 0 test(s) that failed last time in 0 test project(s)"
            + "; left out Else.Tests, which is not a test project in this repository"
            + "; left out Gone, which is not a test project in this repository"
            + "; left out Lib, which is not a test project in this repository",
            plan.Summary);
    }

    [Fact]
    public async Task Past_the_filter_bound_the_rerun_widens_and_says_so()
    {
        await using var engine = await InProcessEngine.StartAsync();
        var names = Enumerable.Range(0, 2001).Select(i => $"Lib.Tests.C{i % 10}.Method{i}").ToArray();
        var pending = await engine.Planner.SelectFailedAsync(
            new Dictionary<Fuse.Paths.RepoPath, string[]> { [engine.Repo.PathOf("Lib.Tests/Lib.Tests.csproj")] = names },
            [], PhaseTimes.None, TestContext.Current.CancellationToken);
        var plan = await engine.Planner.PrepareAsync(pending, PhaseTimes.None, TestContext.Current.CancellationToken);

        var run = Assert.Single(plan.Runs);
        Assert.StartsWith("FullyQualifiedName~Lib.Tests.C", run.Filter, StringComparison.Ordinal);
        Assert.Contains("because a test filter holds at most 2000 names", plan.Summary, StringComparison.Ordinal);
    }
}
