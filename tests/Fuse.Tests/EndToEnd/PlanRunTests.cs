using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.EndToEnd;

/// <summary>
///     How <c>fuse test</c> answers for a plan it runs: every run is a real <c>dotnet test</c> over the standard fixture,
///     and the plan is written here rather than asked of an engine, so a case can name exactly the runs it needs.
/// </summary>
public class PlanRunTests
{
    [Fact]
    public async Task A_test_project_that_does_not_build_is_reported_when_another_one_passes()
    {
        using var repo = FixtureRepo.CreateStandard();
        // App.Tests loses a closing parenthesis and no longer compiles; Lib.Tests builds and its three tests pass.
        repo.Replace("App.Tests/ReportTests.cs", "App.Report.Line(5));", "App.Report.Line(5);");
        TestRun Build(string project) => new(repo.Full($"{project}/{project}.csproj"), project, new TestRunMode.Build(), null, false);
        var plan = new TestPlan([Build("Lib.Tests"), Build("App.Tests")], 4, 4, "ran 4 test(s) affected by your changes out of 4");

        var result = await TestOperation.RunPlanAsync(repo.Root, plan, Environment.TickCount64, TestContext.Current.CancellationToken);

        Assert.Equal(Outcome.ProblemsFound, result.Outcome);
        var lines = result.Text.Split('\n');
        Assert.Contains(lines, l => l.StartsWith("App.Tests/ReportTests.cs(", StringComparison.Ordinal) && l.Contains(": error CS1026: ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("fuse: 0 failed, 3 passed in ", StringComparison.Ordinal));
        // The failed build is the last line, so the passing project's line above it does not read as the answer.
        Assert.StartsWith("fuse: test build failed with 1 error(s) in ", lines[^1], StringComparison.Ordinal);
    }
}
