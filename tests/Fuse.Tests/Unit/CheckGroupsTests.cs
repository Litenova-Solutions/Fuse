using Fuse.Check.Model;
using Fuse.Operations;
using Fuse.Protocol;

namespace Fuse.Tests.Unit;

public class CheckGroupsTests
{
    private static ReportedError Error(string file, int line, ReportedCause? cause = null, bool leftOut = false) =>
        new(new CompilerError(file, line, 1, "CS1061", "no Add"), cause, leftOut);

    private static ReportedCause Removed(string declaration) => new(CauseKind.Removed, declaration);

    [Fact]
    public void Under_the_cap_a_header_marks_each_group_and_order_is_unchanged()
    {
        ReportedError[] errors =
        [
            Error("App/Program.cs", 3, Removed("public int Add(int a, int b)")),
            Error("Lib/Calc.cs", 7),
            Error("Lib.Tests/CalcTests.cs", 6, Removed("public int Add(int a, int b)")),
        ];

        var split = CheckGroups.Split(errors, 20);

        Assert.Equal(
            [
                ("removed: public int Add(int a, int b) (2 error(s))", errors[0], false),
                (null, errors[1], true),
                (null, errors[2], false),
            ],
            split);
    }

    [Fact]
    public void An_error_whose_cause_it_has_alone_keeps_its_cause_line()
    {
        ReportedError[] errors =
        [
            Error("App/Program.cs", 3, Removed("public int Add(int a, int b)")),
            Error("App/Report.cs", 4, Removed("public string Format(long value)")),
        ];

        var split = CheckGroups.Split(errors, 20);

        Assert.All(split, s =>
        {
            Assert.Null(s.Header);
            Assert.True(s.ShowCause);
        });
    }

    [Fact]
    public void Over_the_cap_groups_go_first_largest_first_with_two_members_each()
    {
        var big = Enumerable.Range(1, 10).Select(i => Error($"Big/F{i}.cs", i, Removed("public int Big()"))).ToList();
        var small = Enumerable.Range(1, 3).Select(i => Error($"Small/F{i}.cs", i, Removed("public int Small()"))).ToList();
        var plain = Enumerable.Range(1, 10).Select(i => Error($"Plain/F{i}.cs", i)).ToList();
        var errors = (IReadOnlyList<ReportedError>)[.. plain, .. small, .. big];

        var split = CheckGroups.Split(errors, 20);

        Assert.Equal(2 + 2 + 10, split.Count);
        Assert.Equal("removed: public int Big() (10 error(s), first 2 shown)", split[0].Header);
        Assert.Equal("Big/F1.cs", split[0].Error.Error.Path);
        Assert.Null(split[1].Header);
        Assert.Equal("removed: public int Small() (3 error(s), first 2 shown)", split[2].Header);
        Assert.Null(split[4].Header);
        Assert.Equal("Plain/F1.cs", split[4].Error.Error.Path);
        Assert.All(split.Take(4), s => Assert.False(s.ShowCause));
        Assert.All(split.Skip(4), s => Assert.True(s.ShowCause));
    }

    [Fact]
    public void A_group_with_no_member_shown_prints_no_header()
    {
        var first = Enumerable.Range(1, 5).Select(i => Error($"A/F{i}.cs", i, Removed("public int A()"))).ToList();
        var second = Enumerable.Range(1, 2).Select(i => Error($"B/F{i}.cs", i, Removed("public int B()"))).ToList();
        var errors = (IReadOnlyList<ReportedError>)[.. first, .. second];

        var split = CheckGroups.Split(errors, 2);

        Assert.Equal(2, split.Count);
        Assert.Equal("removed: public int A() (5 error(s), first 2 shown)", split[0].Header);
        Assert.DoesNotContain(split, s => s.Header is not null && s.Header.Contains("B()", StringComparison.Ordinal));
    }

    [Fact]
    public void Ties_break_by_first_appearance()
    {
        ReportedError[] errors =
        [
            Error("B/F1.cs", 1, Removed("public int B()")),
            Error("A/F1.cs", 1, Removed("public int A()")),
            Error("B/F2.cs", 2, Removed("public int B()")),
            Error("A/F2.cs", 2, Removed("public int A()")),
        ];

        var split = CheckGroups.Split(errors, 2);

        Assert.Equal("removed: public int B() (2 error(s), first 2 shown)", split[0].Header);
    }

    [Fact]
    public void Changed_and_removed_causes_of_one_declaration_group_apart()
    {
        ReportedError[] errors =
        [
            Error("A/F1.cs", 1, new ReportedCause(CauseKind.Changed, "public int Thing()")),
            Error("A/F2.cs", 2, Removed("public int Thing()")),
        ];

        var split = CheckGroups.Split(errors, 20);

        Assert.All(split, s =>
        {
            Assert.Null(s.Header);
            Assert.True(s.ShowCause);
        });
    }
}
