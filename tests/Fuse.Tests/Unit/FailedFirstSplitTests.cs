using Fuse.Dotnet;
using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class FailedFirstSplitTests
{
    [Fact]
    public void ExtractPatterns_reads_a_plain_join_unescaped()
    {
        Assert.Equal(
            ["Ns.T.A", "Ns.T.Case(1)"],
            FailedFirstSplit.ExtractPatterns(@"FullyQualifiedName~Ns.T.A|FullyQualifiedName~Ns.T.Case\(1\)"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("(FullyQualifiedName~Ns.T.A)")]
    [InlineData("FullyQualifiedName~Ns.T.A&FullyQualifiedName!=Ns.T.B")]
    [InlineData("FullyQualifiedName~Ns.T.A|Name~B")]
    public void ExtractPatterns_reads_nothing_it_cannot_split(string filter)
    {
        Assert.Empty(FailedFirstSplit.ExtractPatterns(filter));
    }

    [Fact]
    public void TrySplit_builds_the_priority_filter_and_the_rest_without_it()
    {
        var split = FailedFirstSplit.TrySplit("FullyQualifiedName~Ns.T.A|FullyQualifiedName~Ns.T.B", ["Ns.T.A"], out var priority, out var rest);

        Assert.True(split);
        Assert.Equal("FullyQualifiedName~Ns.T.A", priority);
        Assert.Equal("(FullyQualifiedName~Ns.T.A|FullyQualifiedName~Ns.T.B)&FullyQualifiedName!=Ns.T.A", rest);
    }

    [Fact]
    public void TrySplit_above_the_bound_runs_one_stage()
    {
        var patterns = Enumerable.Range(0, FailedFirstSplit.MaxPatterns).Select(i => $"Ns.C{i}.M{i}").ToArray();
        var original = string.Join("|", patterns.Select(p => "FullyQualifiedName~" + p));

        Assert.False(FailedFirstSplit.TrySplit(original, ["Ns.C0.M0"], out _, out _));
    }

    [Fact]
    public void ForPlan_keeps_only_recorded_failures_the_selection_holds()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var project = Path.Combine(repo.Root.Path, "Lib.Tests", "Lib.Tests.csproj");
        LastTestResults.Write(repo.Root,
        [
            (project, "Lib.Tests", new TrxResults(1, 2, 0,
                [new TestFailure("Lib.Tests.CalcTests.Multiplies", "boom", []), new TestFailure("Lib.Tests.Gone.Deleted", "boom", [])])
            {
                Records =
                [
                    new TestRecord("Lib.Tests.CalcTests.Multiplies", TestVerdict.Failed, TimeSpan.Zero),
                    new TestRecord("Lib.Tests.Gone.Deleted", TestVerdict.Failed, TimeSpan.Zero),
                    new TestRecord("Lib.Tests.CalcTests.Adds", TestVerdict.Passed, TimeSpan.Zero),
                ],
            }),
        ]);
        var plan = new TestPlan(
            [new TestRun(project, "Lib.Tests", new TestRunMode.Build(), "FullyQualifiedName~Lib.Tests.CalcTests.", false, null, [], [])],
            1, 3, "ran 1 test(s)");

        var split = FailedFirstSplit.ForPlan(repo.Root, plan)!;

        Assert.Equal(["Lib.Tests.CalcTests.Multiplies"], split[project]);
    }

    [Fact]
    public void ForPlan_strips_theory_rows_to_their_method()
    {
        Assert.Equal("Lib.Tests.CalcTests.Case", FailedFirstSplit.StripTheoryArgs("Lib.Tests.CalcTests.Case(x: 1)"));
        Assert.Equal("Lib.Tests.CalcTests.Adds", FailedFirstSplit.StripTheoryArgs("Lib.Tests.CalcTests.Adds"));
    }

    [Theory]
    [InlineData(true, "FullyQualifiedName~Lib.Tests.CalcTests.")]
    [InlineData(false, null)]
    public void ForPlan_leaves_platform_and_whole_runs_unsplit(bool platform, string? filter)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var project = Path.Combine(repo.Root.Path, "Lib.Tests", "Lib.Tests.csproj");
        LastTestResults.Write(repo.Root,
        [
            (project, "Lib.Tests", new TrxResults(0, 1, 0, [new TestFailure("Lib.Tests.CalcTests.Multiplies", "boom", [])])
            {
                Records = [new TestRecord("Lib.Tests.CalcTests.Multiplies", TestVerdict.Failed, TimeSpan.Zero)],
            }),
        ]);
        var plan = new TestPlan([new TestRun(project, "Lib.Tests", new TestRunMode.Build(), filter, platform, null, [], [])], 1, 3, "ran 1 test(s)");

        Assert.Null(FailedFirstSplit.ForPlan(repo.Root, plan));
    }
}
