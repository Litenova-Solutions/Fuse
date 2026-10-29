using Fuse.Dotnet;
using Fuse.Operations;

namespace Fuse.Tests.Unit;

public class FailureGroupTests
{
    [Fact]
    public void Under_the_cap_a_header_marks_each_group_and_order_is_unchanged()
    {
        var outcome = Results(
            ("Lib.Tests.A.First", "boom\nsecond line"),
            ("Lib.Tests.B.Only", "bang"),
            ("Lib.Tests.C.Second", "boom\nother line"));

        var result = TestOperation.Render(outcome, "ran 3 test(s)", 1.5);

        Assert.Equal(Outcome.ProblemsFound, result.Outcome);
        var lines = result.Text.Split('\n');
        Assert.Equal("2 failure(s) with the same first message line: boom", lines[0]);
        Assert.Equal("FAILED Lib.Tests.A.First", lines[1]);
        Assert.Equal("  boom", lines[2]);
        Assert.Equal("  second line", lines[3]);
        Assert.Equal("FAILED Lib.Tests.B.Only", lines[4]);
        Assert.Equal("FAILED Lib.Tests.C.Second", lines[6]);
        Assert.StartsWith("fuse: 3 failed, 0 passed in 1.5 s; ran 3 test(s)", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Over_the_cap_groups_go_first_with_two_in_detail_and_the_rest_as_names()
    {
        var grouped = Enumerable.Range(0, 7).Select(i => ($"Lib.Tests.G.Test{i}", "boom")).ToList();
        var others = Enumerable.Range(0, 6).Select(i => ($"Lib.Tests.Other{i}.Runs", $"bang {i}")).ToList();
        var outcome = Results([.. grouped, .. others]);

        var result = TestOperation.Render(outcome, "ran 13 test(s)", 2.0);

        Assert.Equal(Outcome.ProblemsFound, result.Outcome);
        var lines = result.Text.Split('\n');
        Assert.Equal("7 failure(s) with the same first message line: boom", lines[0]);
        Assert.Equal("FAILED Lib.Tests.G.Test0", lines[1]);
        Assert.Contains(lines, l => l == "FAILED Lib.Tests.G.Test1");
        Assert.DoesNotContain(lines, l => l == "FAILED Lib.Tests.G.Test2");
        var list = lines.SkipWhile(l => !l.StartsWith("also failed (", StringComparison.Ordinal)).ToList();
        Assert.Equal("also failed (5):", list[0]);
        Assert.Contains("  Lib.Tests.G.Test2", list);
        // Every detailed line starts at column 0 and every listed name is indented, as the evals read them.
        Assert.All(lines.Where(l => l.StartsWith("FAILED ", StringComparison.Ordinal)), l => Assert.Equal(l, l.TrimStart()));
        Assert.StartsWith("fuse: 13 failed, 0 passed in 2.0 s; ran 13 test(s)", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_first_line_never_groups()
    {
        var outcome = Results(("A", ""), ("B", "  \nrest"), ("C", ""));

        var result = TestOperation.Render(outcome, "ran 3 test(s)", 1.0);

        Assert.DoesNotContain("failure(s) with the same", result.Text, StringComparison.Ordinal);
        Assert.Equal(3, result.Text.Split('\n').Count(l => l.StartsWith("FAILED ", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_long_first_line_is_cut_in_the_header_only()
    {
        var first = new string('x', 200);
        var outcome = Results(("A", first + "\nsecond"), ("B", first + "\nother"));

        var result = TestOperation.Render(outcome, "ran 2 test(s)", 1.0);

        Assert.Contains($"2 failure(s) with the same first message line: {new string('x', 160)}...", result.Text, StringComparison.Ordinal);
        Assert.Contains($"  {first}", result.Text, StringComparison.Ordinal);
    }

    private static TrxResults Results(params (string Name, string Message)[] failures) =>
        new(0, failures.Length, 0, failures.Select(f => new TestFailure(f.Name, f.Message, [])).ToList());
}
