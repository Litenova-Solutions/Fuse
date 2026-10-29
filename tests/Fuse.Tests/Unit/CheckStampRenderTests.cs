using Fuse.Check.Model;
using Fuse.Operations;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

public class CheckStampRenderTests
{
    private const string Stamp = "; tree 9f2c41aa07b3 over 87 changed file(s)";

    [Fact]
    public void A_clean_summary_carries_the_stamp_inside_its_counts()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], ["Lib"], 2, false, 0, 0, 0), treeStamp: Stamp);

        Assert.Equal("fuse: no errors introduced (3 file(s) checked; Lib declarations changed, 2 dependent project(s) checked; tree 9f2c41aa07b3 over 87 changed file(s))", result.Text);
    }

    [Fact]
    public void An_error_summary_carries_the_stamp_before_the_file()
    {
        var errors = Enumerable.Range(1, 25)
            .Select(i => new ReportedError(new CompilerError($"src/F{i}.cs", i, 2, "CS0103", "The name 'x' does not exist in the current context")))
            .ToArray();

        var result = CheckOperation.Render(new CheckReport(errors, 25, ["App"], [], 0, false, 25, 25, 0), "/state/last-check-results.txt", treeStamp: Stamp);

        Assert.Equal("fuse: 25 error(s) introduced in 25 file(s), first 20 shown (App); tree 9f2c41aa07b3 over 87 changed file(s); all 25 in /state/last-check-results.txt", result.Text.Split('\n')[^1]);
    }

    [Fact]
    public void A_scoped_summary_carries_the_stamp_after_its_scope()
    {
        var errors = new[] { new ReportedError(new CompilerError("Lib/Calc.cs", 1, 1, "CS1061", "no Add")) };
        var report = new CheckReport(errors, 2, ["Lib"], [], 0, false, 1, 1, 0) { ScopedPaths = ["Lib", "App"] };

        var result = CheckOperation.Render(report, treeStamp: Stamp);

        Assert.Equal("fuse: 1 error(s) introduced in 1 file(s) (Lib); checked Lib, App and what it reaches; tree 9f2c41aa07b3 over 87 changed file(s)", result.Text.Split('\n')[^1]);
    }

    [Fact]
    public void No_stamp_prints_without_one()
    {
        var result = CheckOperation.Render(new CheckReport([], 3, [], [], 0, false, 0, 0, 0));

        Assert.DoesNotContain("tree", result.Text, StringComparison.Ordinal);
    }
}
