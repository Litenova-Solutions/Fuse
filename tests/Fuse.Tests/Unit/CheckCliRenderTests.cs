using System.Text.Json;
using Fuse.Check.Model;
using Fuse.Operations;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

public class CheckCliRenderTests
{
    private static ReportedError Error(string file, int line, ReportedCause? cause = null) =>
        new(new CompilerError(file, line, 1, "CS1061", "no Add"), cause);

    [Fact]
    public void Max_errors_caps_the_errors_printed_and_names_the_file()
    {
        var errors = Enumerable.Range(1, 25)
            .Select(i => Error($"src/F{i}.cs", i))
            .ToArray();
        var result = CheckOperation.Render(new CheckReport(errors, 25, ["App"], [], 0, false, 25, 25, 0), "/state/last-check-results.txt", 5);

        var lines = result.Text.Split('\n');
        Assert.Equal(6, lines.Length);
        Assert.Equal("fuse: 25 error(s) introduced in 25 file(s), first 5 shown (App); all 25 in /state/last-check-results.txt", lines[^1]);
    }

    [Fact]
    public void Every_error_prints_without_a_cap_or_a_file()
    {
        var errors = Enumerable.Range(1, 25)
            .Select(i => Error($"src/F{i}.cs", i))
            .ToArray();
        var result = CheckOperation.Render(new CheckReport(errors, 25, ["App"], [], 0, false, 25, 25, 0), "/state/last-check-results.txt", int.MaxValue);

        var lines = result.Text.Split('\n');
        Assert.Equal(26, lines.Length);
        Assert.Equal("fuse: 25 error(s) introduced in 25 file(s) (App)", lines[^1]);
    }

    [Fact]
    public void Grouped_errors_print_the_cause_once_with_every_member_at_column_zero()
    {
        ReportedError[] errors =
        [
            Error("App/Program.cs", 3, new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)")),
            Error("Lib/Calc.cs", 7),
            Error("Lib.Tests/CalcTests.cs", 6, new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)")),
        ];
        var result = CheckOperation.Render(new CheckReport(errors, 3, ["App"], ["Lib"], 1, false, 3, 3, 0));

        Assert.Equal(
            [
                "removed: public int Add(int a, int b) (2 error(s))",
                "App/Program.cs(3,1): error CS1061: no Add",
                "Lib/Calc.cs(7,1): error CS1061: no Add",
                "Lib.Tests/CalcTests.cs(6,1): error CS1061: no Add",
                "fuse: 3 error(s) introduced in 3 file(s) (App); Lib declarations changed, 1 dependent project(s) checked",
            ],
            result.Text.Split('\n'));
    }

    [Fact]
    public void Json_prints_one_object_per_error_with_the_summary_last()
    {
        ReportedError[] errors =
        [
            Error("App/Program.cs", 3, new ReportedCause(CauseKind.Removed, "public int Add(int a, int b)")),
            Error("Lib/Calc.cs", 7),
        ];
        var result = CheckOperation.Render(new CheckReport(errors, 2, ["App"], [], 0, false, 2, 2, 0), "/state/last-check-results.txt", int.MaxValue, json: true);

        Assert.Equal(1, result.ExitCode);
        var lines = result.Text.Split('\n');
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("App/Program.cs", first.RootElement.GetProperty("file").GetString());
        Assert.Equal(3, first.RootElement.GetProperty("line").GetInt32());
        Assert.Equal("removed: public int Add(int a, int b)", first.RootElement.GetProperty("cause").GetString());
        using var second = JsonDocument.Parse(lines[1]);
        Assert.False(second.RootElement.TryGetProperty("cause", out _));
        Assert.Equal("fuse: 2 error(s) introduced in 2 file(s) (App)", lines[^1]);
    }

    [Fact]
    public void Json_names_no_file_and_keeps_no_cap()
    {
        var errors = Enumerable.Range(1, 25)
            .Select(i => Error($"src/F{i}.cs", i))
            .ToArray();
        var result = CheckOperation.Render(new CheckReport(errors, 25, ["App"], [], 0, false, 25, 25, 0), "/state/last-check-results.txt", int.MaxValue, json: true);

        var lines = result.Text.Split('\n');
        Assert.Equal(26, lines.Length);
        Assert.DoesNotContain("last-check-results.txt", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_json_answer_is_only_the_summary()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], [], 0, false, 0, 0, 0), null, int.MaxValue, json: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("fuse: no errors introduced (3 file(s) checked)", result.Text);
    }

    [Fact]
    public void A_scoped_check_names_its_paths_in_the_error_summary()
    {
        ReportedError[] errors = [Error("Lib/Calc.cs", 7)];
        var report = new CheckReport(errors, 3, ["Lib"], [], 0, false, 1, 1, 0) { ScopedPaths = ["Lib", "App"] };
        var result = CheckOperation.Render(report);

        Assert.Equal("fuse: 1 error(s) introduced in 1 file(s) (Lib); checked Lib, App and what it reaches", result.Text.Split('\n')[^1]);
    }

    [Fact]
    public void A_clean_scoped_check_names_its_paths_before_what_was_checked()
    {
        var report = new CheckReport([], 3, [], [], 0, false, 0, 0, 0) { ScopedPaths = ["Lib"] };
        var result = CheckOperation.Render(report);

        Assert.Equal("fuse: no errors introduced in Lib and what it reaches (3 file(s) checked)", result.Text);
    }
}
