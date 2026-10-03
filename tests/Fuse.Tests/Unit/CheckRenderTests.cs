using Fuse.Check.Model;
using Fuse.Operations;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

public class CheckRenderTests
{
    [Fact]
    public void Clean_report_says_so_with_its_summary()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], ["Lib"], 2, false, 0, 0, 0));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fuse: no errors introduced (3 file(s) checked; Lib declarations changed, 2 dependent project(s) checked)", result.Text);
    }

    [Fact]
    public void Errors_are_listed_in_canonical_form_and_capped()
    {
        var errors = Enumerable.Range(1, 25)
            .Select(i => new ReportedError(new CompilerError($"src/F{i}.cs", i, 2, "CS0103", "The name 'x' does not exist in the current context")))
            .ToArray();
        var result = CheckOperation.Render(new CheckReport(errors, 25, ["App"], [], 0, false, 25, 25, 0));
        Assert.Equal(1, result.ExitCode);
        var lines = result.Text.Split('\n');
        Assert.Equal(21, lines.Length);
        Assert.Equal("src/F1.cs(1,2): error CS0103: The name 'x' does not exist in the current context", lines[0]);
        Assert.Equal("fuse: 25 error(s) introduced in 25 file(s), first 20 shown (App)", lines[^1]);
    }

    [Fact]
    public void The_summary_counts_every_error_found_not_only_the_capped_ones_sent()
    {
        // The engine sends at most 200 errors; the summary still counts all 602, in their 3 files.
        var errors = Enumerable.Range(1, 200)
            .Select(i => new ReportedError(new CompilerError(i == 1 ? "src/A.cs" : "src/B.cs", i, 1, "CS1061", "no Multiply")))
            .ToArray();
        var result = CheckOperation.Render(new CheckReport(errors, 4, ["App", "Tests"], [], 0, false, 602, 3, 0));
        Assert.Equal("fuse: 602 error(s) introduced in 3 file(s), first 20 shown (App, Tests)", result.Text.Split('\n')[^1]);
    }

    [Fact]
    public void Causes_left_out_are_counted_among_the_printed_errors_only()
    {
        // 25 errors in candidates: the first 10 carry a cause and the other 15 lost theirs to the cap, but only 20 print.
        var errors = Enumerable.Range(1, 25)
            .Select(i => i <= 10
                ? new ReportedError(new CompilerError($"src/F{i}.cs", 1, 1, "CS1061", "no Add"), new ReportedCause(CauseKind.Removed, "public int Add()"))
                : new ReportedError(new CompilerError($"src/F{i}.cs", 1, 1, "CS1061", "no Add"), IsCauseLeftOut: true))
            .ToArray();

        var result = CheckOperation.Render(new CheckReport(errors, 25, ["App"], ["Lib"], 1, false, 25, 25, 0));

        Assert.EndsWith("; 10 cause(s) left out", result.Text.Split('\n')[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_cause_is_printed_under_its_error_and_the_summary_says_how_far_the_check_went()
    {
        ReportedError[] errors =
        [
            new(new CompilerError("App/Program.cs", 2, 28, "CS1061", "no Add"), new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)")),
            new(new CompilerError("App/Report.cs", 4, 9, "CS1503", "wrong argument"), new ReportedCause(CauseKind.Changed, "public string Format(long value)")),
            new(new CompilerError("Lib/Calc.cs", 7, 5, "CS0103", "no x")),
            new(new CompilerError("App/Other.cs", 9, 1, "CS1061", "no Add"), IsCauseLeftOut: true),
        ];

        var result = CheckOperation.Render(new CheckReport(errors, 640, ["App", "Lib"], ["Lib"], 2, true, 4, 4, 0));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            [
                "App/Program.cs(2,28): error CS1061: no Add",
                "  removed: public int Add(int a, int b)",
                "App/Report.cs(4,9): error CS1503: wrong argument",
                "  changed: public string Format(long value)",
                "Lib/Calc.cs(7,5): error CS0103: no x",
                "App/Other.cs(9,1): error CS1061: no Add",
                "fuse: 4 error(s) introduced in 4 file(s) (App, Lib); Lib declarations changed, 2 dependent project(s) checked; checked whole projects; 1 cause(s) left out",
            ],
            result.Text.Split('\n'));
    }

    [Fact]
    public void Errors_left_to_other_sessions_are_counted_in_the_summary()
    {
        ReportedError[] errors = [new(new CompilerError("App/Program.cs", 2, 28, "CS1061", "no Add"))];

        var result = CheckOperation.Render(new CheckReport(errors, 3, ["App"], ["Lib"], 1, false, 1, 1, 2));

        Assert.Equal(1, result.ExitCode);
        Assert.EndsWith("; 2 error(s) from other sessions' edits left out", result.Text.Split('\n')[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_session_whose_edits_introduced_nothing_is_answered_clean()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], [], 0, false, 0, 0, 2));

        // The hook stays silent on a clean answer, so the session is not sent after errors it did not cause.
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fuse: no errors introduced by this session's edits (3 file(s) checked; 2 error(s) from other sessions' edits left out)", result.Text);
    }
}
