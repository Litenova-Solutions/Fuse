using Fuse.Check.Model;
using Fuse.Engine;
using Fuse.Failures;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Testing.Model;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     The one place a check's result and a test plan become what the client receives. A field the mapper drops or swaps
///     is a count, a cause line or a test run the agent never sees, so every field is pinned here, and so is the JSON the
///     pipe carries.
/// </summary>
public class ResponseMapperTests
{
    private static readonly CompilerError Removed = new("App/Program.cs", 2, 28, "CS1061", "'Calc' does not contain a definition for 'Add'");
    private static readonly CompilerError Changed = new("App/Report.cs", 5, 9, "CS1503", "cannot convert from 'int' to 'long'");
    private static readonly CompilerError Analyzed = new("Lib/Calc.cs", 7, 5, "CA1825", "Avoid zero-length array allocations", FromAnalyzer: true);

    private static readonly CompilerError LeftOut = new("App/Other.cs", 9, 1, "CS1061", "no Add");

    private static readonly CheckResult Result = new(
        [
            new IntroducedError(Removed, new Cause.Removed("public int Add(int a, int b)")),
            new IntroducedError(Changed, new Cause.Changed("public static string Format(long value)")),
            new IntroducedError(Analyzed),
            new IntroducedError(LeftOut, IsCauseLeftOut: true),
        ],
        FilesChecked: 7,
        Projects: ["App", "Lib"],
        DeclarationsChangedIn: ["Lib"],
        DependentProjectsChecked: 3,
        CheckedWholeProjects: true);

    // The project files are never read, so any root will do; the wire carries each as its absolute path.
    private static readonly RepoRoot Root = FixtureRepo.CheckoutRoot;

    private static readonly TestPlanResult Plan = new(
        [
            new PlannedRun(Root.PathOf("Lib.Tests/Lib.Tests.csproj"), "Lib.Tests(net8.0)", new RunMode.Shadow("C:/state/shadow/Lib.Tests-net8.0/Lib.Tests.dll"), "FullyQualifiedName~Lib.Tests.CalcTests.", false, "C:/repo/Lib.Tests/test.runsettings"),
            new PlannedRun(Root.PathOf("App.Tests/App.Tests.csproj"), "App.Tests", new RunMode.Build(), null, false, null),
            new PlannedRun(Root.PathOf("Mtp.Tests/Mtp.Tests.csproj"), "Mtp.Tests", new RunMode.Build(), null, true, null),
        ],
        SelectedTests: 12,
        TotalTests: 40,
        Summary: "ran 12 test(s) affected by your changes out of 40 (whole projects where App uses the changed code and runs behind an application host); fuse test --all runs everything");

    [Fact]
    public void A_check_result_maps_to_its_report_field_by_field()
    {
        var report = ResponseMapper.Report(Result);

        Assert.Equal([Removed, Changed, Analyzed, LeftOut], report.Errors.Select(e => e.Error));
        Assert.Equal(new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)"), report.Errors[0].Cause);
        Assert.Equal(new ReportedCause(CauseKind.Changed, "public static string Format(long value)"), report.Errors[1].Cause);
        Assert.Equal(7, report.FilesChecked);
        Assert.Equal(["App", "Lib"], report.Projects);
        Assert.Equal(["Lib"], report.DeclarationsChangedIn);
        Assert.Equal(3, report.DependentProjectsChecked);
        Assert.True(report.CheckedWholeProjects);
        Assert.Equal([false, false, false, true], report.Errors.Select(e => e.IsCauseLeftOut));
    }

    [Fact]
    public void An_error_with_no_cause_is_reported_with_none()
    {
        var report = ResponseMapper.Report(Result);

        Assert.Null(report.Errors[2].Cause);
        Assert.True(report.Errors[2].Error.FromAnalyzer);
    }

    [Fact]
    public void An_answered_check_carries_its_report_across_the_pipe()
    {
        var line = ProtocolJson.Serialize(ResponseMapper.Answered(Result));
        var read = ProtocolJson.ReadResponse(line);

        var report = Assert.IsType<EngineResponse.CheckAnswered>(read).Report;
        Assert.Equal([Removed, Changed, Analyzed, LeftOut], report.Errors.Select(e => e.Error));
        Assert.Equal([new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)"), new ReportedCause(CauseKind.Changed, "public static string Format(long value)"), null, null], report.Errors.Select(e => e.Cause));
        Assert.Equal([false, false, false, true], report.Errors.Select(e => e.IsCauseLeftOut));
        Assert.Equal((7, 3, true), (report.FilesChecked, report.DependentProjectsChecked, report.CheckedWholeProjects));
        Assert.Equal(["Lib"], report.DeclarationsChangedIn);
        // The field names are the wire contract; a change here changes what this build's engine sends.
        Assert.StartsWith("{\"status\":\"CheckAnswered\",\"report\":{", line, StringComparison.Ordinal);
        foreach (var name in new[] { "\"errors\"", "\"cause\"", "\"kind\":\"Removed\"", "\"fromAnalyzer\":true", "\"declarationsChangedIn\"", "\"checkedWholeProjects\"", "\"isCauseLeftOut\":true" })
            Assert.Contains(name, line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_plan_result_maps_to_its_plan_field_by_field()
    {
        var plan = ResponseMapper.Plan(Plan);

        Assert.Equal(
            [
                new TestRun(Path.Combine(Root.Path, "Lib.Tests", "Lib.Tests.csproj"), "Lib.Tests(net8.0)", new TestRunMode.Shadow("C:/state/shadow/Lib.Tests-net8.0/Lib.Tests.dll"), "FullyQualifiedName~Lib.Tests.CalcTests.", false, "C:/repo/Lib.Tests/test.runsettings"),
                new TestRun(Path.Combine(Root.Path, "App.Tests", "App.Tests.csproj"), "App.Tests", new TestRunMode.Build(), null, false, null),
                new TestRun(Path.Combine(Root.Path, "Mtp.Tests", "Mtp.Tests.csproj"), "Mtp.Tests", new TestRunMode.Build(), null, true, null),
            ],
            plan.Runs);
        Assert.Equal((12, 40), (plan.SelectedTests, plan.TotalTests));
        Assert.Equal(Plan.Summary, plan.Summary);
    }

    [Fact]
    public void An_answered_test_plan_carries_both_run_modes_across_the_pipe()
    {
        var line = ProtocolJson.Serialize(ResponseMapper.Answered(Plan));
        var read = ProtocolJson.ReadResponse(line);

        var plan = Assert.IsType<EngineResponse.PlanAnswered>(read).Plan;
        Assert.Equal(ResponseMapper.Plan(Plan).Runs, plan.Runs);
        Assert.Equal("C:/state/shadow/Lib.Tests-net8.0/Lib.Tests.dll", Assert.IsType<TestRunMode.Shadow>(plan.Runs[0].Mode).Assembly);
        Assert.IsType<TestRunMode.Build>(plan.Runs[1].Mode);
        Assert.Equal((12, 40, Plan.Summary), (plan.SelectedTests, plan.TotalTests, plan.Summary));
        // The field names are the wire contract; a change here changes what this build's engine sends.
        Assert.StartsWith("{\"status\":\"PlanAnswered\",\"plan\":{", line, StringComparison.Ordinal);
        foreach (var name in new[] { "\"summary\"", "\"mode\":{\"kind\":\"Shadow\",\"assembly\":", "\"mode\":{\"kind\":\"Build\"}", "\"usesTestingPlatform\":true", "\"runSettings\":\"C:/repo/Lib.Tests/test.runsettings\"" })
            Assert.Contains(name, line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_that_ended_a_request_is_unanswered_with_its_code_and_message()
    {
        var line = ProtocolJson.Serialize(ResponseMapper.Unanswered(new FuseException(ErrorCode.RestoreNeeded, "run dotnet restore")));

        Assert.Equal("{\"status\":\"Unanswered\",\"code\":\"RestoreNeeded\",\"message\":\"run dotnet restore\"}", line);
        Assert.Equal(new EngineResponse.Unanswered(ErrorCode.RestoreNeeded, "run dotnet restore"), ProtocolJson.ReadResponse(line));
    }
}
