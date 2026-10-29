using Fuse.Dotnet;
using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

/// <summary>
///     A project whose split would name a theory row runs as one stage: the stripped name cannot exclude the row from
///     the rest, so each row would be counted twice. Plain names still split.
/// </summary>
public class FailedFirstSplitStrippedTests
{
    [Fact]
    public void ForPlan_with_a_theory_row_in_the_selection_runs_one_stage()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var project = Path.Combine(repo.Root.Path, "Lib.Tests", "Lib.Tests.csproj");
        LastTestResults.Write(repo.Root,
        [
            (project, "Lib.Tests", new TrxResults(0, 1, 0,
                [new TestFailure("Lib.Tests.CalcTests.Case(x: 1)", "boom", [])])
            {
                Records =
                [
                    new TestRecord("Lib.Tests.CalcTests.Case(x: 1)", TestVerdict.Failed, TimeSpan.Zero),
                ],
            }),
        ]);
        var plan = new TestPlan(
            [new TestRun(project, "Lib.Tests", new TestRunMode.Build(), "FullyQualifiedName~Lib.Tests.CalcTests.", false, null, [], [])],
            1, 3, "ran 1 test(s)");

        Assert.Null(FailedFirstSplit.ForPlan(repo.Root, plan));
    }

    [Fact]
    public void ForPlan_with_only_plain_failures_in_the_selection_still_splits()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var project = Path.Combine(repo.Root.Path, "Lib.Tests", "Lib.Tests.csproj");
        LastTestResults.Write(repo.Root,
        [
            (project, "Lib.Tests", new TrxResults(0, 2, 0,
                [new TestFailure("Lib.Tests.CalcTests.Multiplies", "boom", []), new TestFailure("Lib.Tests.Other.Case(x: 1)", "boom", [])])
            {
                Records =
                [
                    new TestRecord("Lib.Tests.CalcTests.Multiplies", TestVerdict.Failed, TimeSpan.Zero),
                    new TestRecord("Lib.Tests.Other.Case(x: 1)", TestVerdict.Failed, TimeSpan.Zero),
                ],
            }),
        ]);
        var plan = new TestPlan(
            [new TestRun(project, "Lib.Tests", new TestRunMode.Build(), "FullyQualifiedName~Lib.Tests.CalcTests.", false, null, [], [])],
            1, 3, "ran 1 test(s)");

        var split = FailedFirstSplit.ForPlan(repo.Root, plan)!;

        Assert.Equal(["Lib.Tests.CalcTests.Multiplies"], split[project]);
    }
}
