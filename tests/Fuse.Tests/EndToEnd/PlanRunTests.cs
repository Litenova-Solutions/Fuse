using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Testing;
using Fuse.Testing.Model;
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
        TestRun Build(string project) => new(repo.Full($"{project}/{project}.csproj"), project, new TestRunMode.Build(), null, false, null);
        var plan = new TestPlan([Build("Lib.Tests"), Build("App.Tests")], 4, 4, "ran 4 test(s) affected by your changes out of 4");

        var result = await TestOperation.RunPlanAsync(repo.Root, plan, Environment.TickCount64, TestContext.Current.CancellationToken);

        Assert.Equal(Outcome.ProblemsFound, result.Outcome);
        var lines = result.Text.Split('\n');
        Assert.Contains(lines, l => l.StartsWith("App.Tests/ReportTests.cs(", StringComparison.Ordinal) && l.Contains(": error CS1026: ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("fuse: 0 failed, 3 passed in ", StringComparison.Ordinal));
        // The failed build is the last line, so the passing project's line above it does not read as the answer.
        Assert.StartsWith("fuse: test build failed with 1 error(s) in ", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_filter_above_8000_characters_runs_only_the_selected_test_on_a_shadow_run_and_with_msbuild()
    {
        using var repo = FixtureRepo.CreateStandard();
        var filter = LongFilter();
        var project = repo.Full("Lib.Tests/Lib.Tests.csproj");
        // The standard fixture is built, so its test assembly can run the way a shadow run's copy does.
        var shadow = new TestRun(project, "Lib.Tests", new TestRunMode.Shadow(repo.Full("Lib.Tests/bin/Debug/net10.0/Lib.Tests.dll")), filter, false, null);
        var build = shadow with { Mode = new TestRunMode.Build() };

        foreach (var (run, mode) in new[] { (shadow, "without MSBuild"), (build, "built with MSBuild") })
        {
            var result = await TestOperation.RunPlanAsync(repo.Root, new TestPlan([run], 1, 4, "ran 1 test(s)"), Environment.TickCount64, TestContext.Current.CancellationToken);
            Assert.Equal(Outcome.Clean, result.Outcome);
            Assert.Matches($@"^fuse: 0 failed, 1 passed in [0-9.]+ s; ran 1 test\(s\); {mode}$", result.Text);
        }
    }

    [Fact]
    public async Task A_shadow_run_keeps_the_settings_of_the_project_runsettings_file()
    {
        using var repo = FixtureRepo.CreateStandard();
        // The project's own settings narrow every run to Greets, as dotnet test on the project would.
        repo.Write("Lib.Tests/probe.runsettings", "<RunSettings><RunConfiguration><TestCaseFilter>FullyQualifiedName~Greets</TestCaseFilter></RunConfiguration></RunSettings>");
        var shadow = new TestRun(
            repo.Full("Lib.Tests/Lib.Tests.csproj"), "Lib.Tests", new TestRunMode.Shadow(repo.Full("Lib.Tests/bin/Debug/net10.0/Lib.Tests.dll")), null, false, repo.Full("Lib.Tests/probe.runsettings"));

        // A whole selection and one with a filter: the project's filter applies to both.
        foreach (var run in new[] { shadow, shadow with { Filter = "FullyQualifiedName~Lib.Tests." } })
        {
            var result = await TestOperation.RunPlanAsync(repo.Root, new TestPlan([run], 3, 4, "ran 3 test(s)"), Environment.TickCount64, TestContext.Current.CancellationToken);
            Assert.Matches(@"^fuse: 0 failed, 1 passed in [0-9.]+ s; ran 3 test\(s\); without MSBuild$", result.Text);
        }
    }

    /// <summary>A filter of Multiplies and 400 patterns no test matches, longer than the 8,000 characters a command line once bounded it to.</summary>
    private static string LongFilter()
    {
        string[] patterns = ["Lib.Tests.CalcTests.Multiplies", .. Enumerable.Range(0, 400).Select(i => $"Lib.Tests.NoSuchTestClassAnywhere.NoSuchMethod{i}")];
        var filter = TestFilter.For(new TestSelection.Methods([.. patterns]));
        Assert.Equal(FilterCollapse.None, filter.Collapse);
        Assert.True(filter.Expression!.Length > 8000);
        return filter.Expression;
    }
}
