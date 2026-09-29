using Fuse.Check.Model;
using Fuse.Operations;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

public class CheckRenderTests
{
    [Fact]
    public void Clean_report_says_so_with_scope()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], ["Lib"], 2, false, 0));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fuse: no errors introduced (3 file(s) checked; Lib declarations changed, 2 dependent project(s) checked)", result.Text);
    }

    [Fact]
    public void Errors_are_listed_in_canonical_form_and_capped()
    {
        var errors = Enumerable.Range(1, 25)
            .Select(i => new ReportedError(new CompilerError($"src/F{i}.cs", i, 2, "CS0103", "The name 'x' does not exist in the current context")))
            .ToArray();
        var result = CheckOperation.Render(new CheckReport(errors, 25, ["App"], [], 0, false, 0));
        Assert.Equal(1, result.ExitCode);
        var lines = result.Text.Split('\n');
        Assert.Equal(21, lines.Length);
        Assert.Equal("src/F1.cs(1,2): error CS0103: The name 'x' does not exist in the current context", lines[0]);
        Assert.Equal("fuse: 25 error(s) introduced in 25 file(s), first 20 shown (App)", lines[^1]);
    }

    [Fact]
    public void A_cause_is_printed_under_its_error_and_the_summary_says_how_far_the_check_went()
    {
        ReportedError[] errors =
        [
            new(new CompilerError("App/Program.cs", 2, 28, "CS1061", "no Add"), new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)")),
            new(new CompilerError("App/Report.cs", 4, 9, "CS1503", "wrong argument"), new ReportedCause(CauseKind.Changed, "public string Format(long value)")),
            new(new CompilerError("Lib/Calc.cs", 7, 5, "CS0103", "no x")),
        ];

        var result = CheckOperation.Render(new CheckReport(errors, 640, ["App", "Lib"], ["Lib"], 2, true, 3));

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(
            [
                "App/Program.cs(2,28): error CS1061: no Add",
                "  removed: public int Add(int a, int b)",
                "App/Report.cs(4,9): error CS1503: wrong argument",
                "  changed: public string Format(long value)",
                "Lib/Calc.cs(7,5): error CS0103: no x",
                "fuse: 3 error(s) introduced in 3 file(s) (App, Lib); Lib declarations changed, 2 dependent project(s) checked; checked whole projects; 3 cause(s) left out",
            ],
            result.Text.Split('\n'));
    }
}
