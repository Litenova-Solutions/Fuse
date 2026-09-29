using Fuse.Check.Model;
using Fuse.Engine;
using Fuse.Protocol;
using Fuse.Testing.Model;

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

    private static readonly CheckResult Result = new(
        [
            new IntroducedError(Removed, new Cause.Removed("public int Add(int a, int b)")),
            new IntroducedError(Changed, new Cause.Changed("public static string Format(long value)")),
            new IntroducedError(Analyzed),
        ],
        FilesChecked: 7,
        Projects: ["App", "Lib"],
        DeclarationsChangedIn: ["Lib"],
        DependentProjectsChecked: 3,
        CheckedWholeProjects: true,
        CausesLeftOut: 4);

    private static readonly TestPlanResult Plan = new(
        [
            new PlannedRun("C:/repo/Lib.Tests/Lib.Tests.csproj", "Lib.Tests(net8.0)", new RunMode.Shadow("C:/state/shadow/Lib.Tests-net8.0/Lib.Tests.dll"), "FullyQualifiedName~Lib.Tests.CalcTests.", false),
            new PlannedRun("C:/repo/App.Tests/App.Tests.csproj", "App.Tests", new RunMode.Build(), null, false),
            new PlannedRun("C:/repo/Mtp.Tests/Mtp.Tests.csproj", "Mtp.Tests", new RunMode.Build(), null, true),
        ],
        SelectedTests: 12,
        TotalTests: 40,
        Summary: "ran 12 test(s) affected by your changes out of 40 (whole projects where the change reaches App, which runs behind a host); fuse test --all runs everything");

    [Fact]
    public void A_check_result_maps_to_its_report_field_by_field()
    {
        var report = ResponseMapper.Report(Result);

        Assert.Equal([Removed, Changed, Analyzed], report.Errors.Select(e => e.Error));
        Assert.Equal(new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)"), report.Errors[0].Cause);
        Assert.Equal(new ReportedCause(CauseKind.Changed, "public static string Format(long value)"), report.Errors[1].Cause);
        Assert.Equal(7, report.FilesChecked);
        Assert.Equal(["App", "Lib"], report.Projects);
        Assert.Equal(["Lib"], report.DeclarationsChangedIn);
        Assert.Equal(3, report.DependentProjectsChecked);
        Assert.True(report.CheckedWholeProjects);
        Assert.Equal(4, report.CausesLeftOut);
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

        Assert.Equal(ResponseStatus.Ok, read?.Status);
        var report = Assert.IsType<CheckReport>(read?.Check);
        Assert.Equal([Removed, Changed, Analyzed], report.Errors.Select(e => e.Error));
        Assert.Equal([new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)"), new ReportedCause(CauseKind.Changed, "public static string Format(long value)"), null], report.Errors.Select(e => e.Cause));
        Assert.Equal((7, 3, true, 4), (report.FilesChecked, report.DependentProjectsChecked, report.CheckedWholeProjects, report.CausesLeftOut));
        Assert.Equal(["Lib"], report.DeclarationsChangedIn);
        // The field names are the ones the Renames table in docs/architecture.md gives the wire.
        foreach (var name in new[] { "\"errors\"", "\"cause\"", "\"kind\":\"Removed\"", "\"fromAnalyzer\":true", "\"declarationsChangedIn\"", "\"checkedWholeProjects\"", "\"causesLeftOut\"" })
            Assert.Contains(name, line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_test_plan_result_maps_to_its_plan_field_by_field()
    {
        var plan = ResponseMapper.Plan(Plan);

        Assert.Equal(
            [
                new TestRun("C:/repo/Lib.Tests/Lib.Tests.csproj", "Lib.Tests(net8.0)", new TestRunMode.Shadow("C:/state/shadow/Lib.Tests-net8.0/Lib.Tests.dll"), "FullyQualifiedName~Lib.Tests.CalcTests.", false),
                new TestRun("C:/repo/App.Tests/App.Tests.csproj", "App.Tests", new TestRunMode.Build(), null, false),
                new TestRun("C:/repo/Mtp.Tests/Mtp.Tests.csproj", "Mtp.Tests", new TestRunMode.Build(), null, true),
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

        Assert.Equal(ResponseStatus.Ok, read?.Status);
        var plan = Assert.IsType<TestPlan>(read?.Tests);
        Assert.Equal(ResponseMapper.Plan(Plan).Runs, plan.Runs);
        Assert.Equal("C:/state/shadow/Lib.Tests-net8.0/Lib.Tests.dll", Assert.IsType<TestRunMode.Shadow>(plan.Runs[0].Mode).Assembly);
        Assert.IsType<TestRunMode.Build>(plan.Runs[1].Mode);
        Assert.Equal((12, 40, Plan.Summary), (plan.SelectedTests, plan.TotalTests, plan.Summary));
        // The field names are the ones the Renames table in docs/architecture.md gives the wire.
        foreach (var name in new[] { "\"summary\"", "\"mode\":{\"kind\":\"Shadow\",\"assembly\":", "\"mode\":{\"kind\":\"Build\"}", "\"usesTestingPlatform\":true" })
            Assert.Contains(name, line, StringComparison.Ordinal);
    }
}
