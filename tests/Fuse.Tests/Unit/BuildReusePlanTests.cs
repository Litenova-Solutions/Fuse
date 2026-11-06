using Fuse.Dotnet;
using Fuse.Operations;
using Fuse.Protocol;
using Fuse.Tests.Fixtures;

namespace Fuse.Tests.Unit;

public class BuildReusePlanTests
{
    [Fact]
    public void A_recorded_run_is_read_back_whole()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var project = Path.Combine(repo.Root.Path, "Lib.Tests", "Lib.Tests.csproj");
        var failure = new TestFailure("Lib.Tests.CalcTests.Multiplies", "boom", ["at CalcTests in Lib/Calc.cs:line 8"]);
        var aggregate = new TrxResults(2, 1, 0, [failure]);
        var finished = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

        LastTestResults.WriteRun(repo.Root, "tree", "full", "assets", "key", finished, 4.5, "2 tests; without MSBuild", aggregate,
            [(project, "Lib.Tests", new TrxResults(2, 1, 0, [failure])
            {
                Records =
                [
                    new TestRecord("Lib.Tests.CalcTests.Multiplies", TestVerdict.Failed, TimeSpan.FromMilliseconds(3)),
                    new TestRecord("Lib.Tests.CalcTests.Adds", TestVerdict.Passed, TimeSpan.FromMilliseconds(1)),
                ],
            })],
            [new OperationResult(Outcome.ProblemsFound, "fuse: test build failed")]);

        var stored = LastTestResults.ReadRun(repo.Root)!.Value;
        Assert.Equal("tree", stored.TreeHash);
        Assert.Equal("full", stored.FullHash);
        Assert.Equal("assets", stored.AssetsHash);
        Assert.Equal("key", stored.PlanKey);
        Assert.Equal(finished, stored.FinishedUtc);
        Assert.Equal(4.5, stored.Seconds);
        Assert.Equal("2 tests; without MSBuild", stored.Summary);
        Assert.Equal((2, 1, 0), (stored.Aggregate.Passed, stored.Aggregate.Failed, stored.Aggregate.Skipped));
        AssertFailure(failure, Assert.Single(stored.Aggregate.Failures));
        var run = Assert.Single(stored.Runs);
        Assert.Equal((project, "Lib.Tests"), (run.Project, run.Name));
        Assert.Equal((2, 1, 0), (run.Outcome.Passed, run.Outcome.Failed, run.Outcome.Skipped));
        AssertFailure(failure, Assert.Single(run.Outcome.Failures));
        Assert.Equal(
            [("Lib.Tests.CalcTests.Multiplies", TestVerdict.Failed, 3.0), ("Lib.Tests.CalcTests.Adds", TestVerdict.Passed, 1.0)],
            run.Outcome.Records.Select(t => (t.Name, t.Verdict, t.Duration.TotalMilliseconds)));
        var problem = Assert.Single(stored.Problems);
        Assert.Equal(Outcome.ProblemsFound, problem.Outcome);
        Assert.Equal("fuse: test build failed", problem.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"version":2}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"treeHash":"a","fullHash":"b","assetsHash":"c","planKey":"k","finishedUtc":"2026-11-06T12:00:00Z","seconds":1,"summary":"s","aggregate":{"passed":0,"failed":0,"skipped":0,"failures":[]},"runs":[],"problems":[{"outcome":"Bogus","text":"t"}]}""")]
    public void Missing_malformed_and_other_versions_read_as_null(string? content)
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        if (content is not null)
        {
            Directory.CreateDirectory(repo.Root.StateDirectory);
            File.WriteAllText(Path.Combine(repo.Root.StateDirectory, "last-test-run.json"), content);
        }

        Assert.Null(LastTestResults.ReadRun(repo.Root));
    }

    [Fact]
    public void The_plan_key_ignores_shadow_folders_but_nothing_else()
    {
        var shadowA = new TestRun("/r/T/T.csproj", "T", new TestRunMode.Shadow("/s/g1/T.dll"), "F", false, null, [], []);
        var shadowB = shadowA with { Mode = new TestRunMode.Shadow("/s/g2/T.dll") };
        var plan = new TestPlan([shadowA], 1, 4, "s");

        Assert.Equal(TestOperation.PlanKey(plan, null), TestOperation.PlanKey(new TestPlan([shadowB], 1, 4, "s"), null));
        Assert.NotEqual(TestOperation.PlanKey(plan, null), TestOperation.PlanKey(new TestPlan([shadowA with { Filter = "G" }], 1, 4, "s"), null));
        Assert.NotEqual(TestOperation.PlanKey(plan, null), TestOperation.PlanKey(new TestPlan([shadowA with { Mode = new TestRunMode.Build() }], 1, 4, "s"), null));
        Assert.NotEqual(TestOperation.PlanKey(plan, null), TestOperation.PlanKey(new TestPlan([shadowA], 2, 4, "s"), null));
        Assert.NotEqual(TestOperation.PlanKey(plan, null), TestOperation.PlanKey(new TestPlan([shadowA], 1, 4, "other"), null));

        var second = new TestRun("/r/U/U.csproj", "U", new TestRunMode.Build(), null, false, null, [], []);
        Assert.NotEqual(
            TestOperation.PlanKey(new TestPlan([shadowA, second], 2, 4, "s"), null),
            TestOperation.PlanKey(new TestPlan([second, shadowA], 2, 4, "s"), null));

        var scope = new TestScopeArguments(["/r/T"], ["-c", "Release"], true, "Release", null);
        Assert.NotEqual(TestOperation.PlanKey(plan, null), TestOperation.PlanKey(plan, scope));
        Assert.NotEqual(
            TestOperation.PlanKey(plan, scope),
            TestOperation.PlanKey(plan, scope with { BuildSwitches = ["-c", "Debug"] }));
    }

    [Fact]
    public void An_identical_plan_prints_the_stored_run_as_reused()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var project = Path.Combine(repo.Root.Path, "Lib.Tests", "Lib.Tests.csproj");
        var run = new TestRun(project, "Lib.Tests", new TestRunMode.Build(), null, false, null, [], []);
        var plan = new TestPlan([run], 3, 4, "ran 3 test(s)");
        var key = TestOperation.PlanKey(plan, null);
        var failure = new TestFailure("Lib.Tests.CalcTests.Multiplies", "boom", []);
        LastTestResults.WriteRun(repo.Root, "9f2c41aa07b3", "full", "assets", key, DateTime.UtcNow.AddSeconds(-14), 4.0, "ran 3 test(s); built with MSBuild",
            new TrxResults(2, 1, 0, [failure]),
            [(project, "Lib.Tests", new TrxResults(2, 1, 0, [failure]) { Records = [new TestRecord("Lib.Tests.CalcTests.Multiplies", TestVerdict.Failed, TimeSpan.Zero)] })],
            []);

        var answer = TestOperation.ReusedTestRun(repo.Root, ("9f2c41aa07b3", "full", "assets", 0), key, live: null)!;

        Assert.Equal(Outcome.ProblemsFound, answer.Outcome);
        var lines = answer.Text.Split('\n');
        Assert.Matches(@"^fuse: test run reused from \d+ s ago; tree 9f2c41aa07b3$", lines[0]);
        Assert.Contains(lines, l => l == "FAILED Lib.Tests.CalcTests.Multiplies");
        Assert.Contains("1 failed, 2 passed", lines[^1], StringComparison.Ordinal);
        Assert.Contains("Multiplies", File.ReadAllText(Path.Combine(repo.Root.StateDirectory, "last-test-results.txt")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_with_another_plan_or_tree_runs_again()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        LastTestResults.WriteRun(repo.Root, "tree", "full", "assets", "key", DateTime.UtcNow, 1.0, "s",
            new TrxResults(1, 0, 0, []), [("/r/T/T.csproj", "T", new TrxResults(1, 0, 0, []))], []);

        Assert.Null(TestOperation.ReusedTestRun(repo.Root, ("tree", "full", "assets", 0), "other", live: null));
        Assert.Null(TestOperation.ReusedTestRun(repo.Root, ("other", "full", "assets", 0), "key", live: null));
        Assert.Null(TestOperation.ReusedTestRun(repo.Root, ("tree", "other", "assets", 0), "key", live: null));
    }

    [Fact]
    public void A_reused_run_streams_its_failures_to_live()
    {
        using var repo = FixtureRepo.CreateEmpty(new Dictionary<string, string> { ["a.txt"] = "x" });
        var failure = new TestFailure("Lib.Tests.CalcTests.Multiplies", "boom", []);
        LastTestResults.WriteRun(repo.Root, "tree", "full", "assets", "key", DateTime.UtcNow, 4.0, "s",
            new TrxResults(0, 1, 0, [failure]), [("/r/T/T.csproj", "T", new TrxResults(0, 1, 0, [failure]))], []);
        var live = new StringWriter();

        var answer = TestOperation.ReusedTestRun(repo.Root, ("tree", "full", "assets", 0), "key", live)!;

        Assert.Contains("FAILED Lib.Tests.CalcTests.Multiplies", live.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("fuse: test run reused from ", answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("FAILED", answer.Text, StringComparison.Ordinal);
        Assert.Contains("1 failed, 0 passed", answer.Text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One failure as a stored run holds it: name, message and frames in order. A failure record compares its
    ///     frame list by the instance it holds, and a run read back from disk holds a new one, so the round trip is
    ///     read field by field instead of by the record.
    /// </summary>
    private static void AssertFailure(TestFailure expected, TestFailure actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.Frames, actual.Frames);
    }
}
