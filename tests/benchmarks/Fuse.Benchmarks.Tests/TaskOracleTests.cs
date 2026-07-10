using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// C4: the oracle-task extraction logic. A task qualifies only when the PR's new or changed tests fail on the
// base commit and pass on the merge commit, and are not flaky. These tests pin that decision and the runner
// output parsing, and exercise the end-to-end verify over a synthetic PR through an injected test runner (so no
// real suite runs).
public sealed class TaskOracleTests
{
    private static TestRunOutcome Ran(int passed, int failed) => new(Executed: true, passed, failed);

    [Fact]
    public void Fail_on_base_green_on_merge_not_flaky_is_verified()
    {
        var v = TaskOracle.Decide(onBase: Ran(0, 3), onMerge: Ran(3, 0), onMergeRerun: Ran(3, 0));
        Assert.True(v.Verified, v.Reason);
    }

    [Fact]
    public void Base_not_executing_counts_as_failure_on_base()
    {
        // A new test referencing new code does not compile on base: that is a failure on base, not a disqualifier.
        var v = TaskOracle.Decide(onBase: TestRunOutcome.DidNotExecute, onMerge: Ran(2, 0), onMergeRerun: Ran(2, 0));
        Assert.True(v.Verified, v.Reason);
    }

    [Fact]
    public void Green_on_base_is_not_verified()
    {
        var v = TaskOracle.Decide(onBase: Ran(3, 0), onMerge: Ran(3, 0), onMergeRerun: Ran(3, 0));
        Assert.False(v.Verified);
    }

    [Fact]
    public void Not_green_on_merge_is_not_verified()
    {
        var v = TaskOracle.Decide(onBase: Ran(0, 3), onMerge: Ran(2, 1), onMergeRerun: Ran(2, 1));
        Assert.False(v.Verified);
    }

    [Fact]
    public void Flaky_merge_rerun_is_excluded()
    {
        var v = TaskOracle.Decide(onBase: Ran(0, 3), onMerge: Ran(3, 0), onMergeRerun: Ran(2, 1));
        Assert.False(v.Verified);
    }

    [Fact]
    public void Parses_a_vstest_summary_and_a_build_failure()
    {
        var summary = "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 3 s";
        var ran = TaskOracle.ParseTestOutput(summary, exitCode: 0);
        Assert.True(ran.Executed);
        Assert.Equal(12, ran.Passed);
        Assert.Equal(0, ran.Failed);

        var buildFail = "error CS0246: The type or namespace name 'Widget' could not be found";
        var didNotRun = TaskOracle.ParseTestOutput(buildFail, exitCode: 1);
        Assert.False(didNotRun.Executed);
    }

    [Fact]
    public async Task VerifyAsync_over_a_synthetic_pr_through_an_injected_runner()
    {
        // Synthetic PR: the base worktree fails the new tests, the merge worktree passes them (stable across runs).
        Task<TestRunOutcome> Runner(string worktree, string filter, CancellationToken ct) =>
            Task.FromResult(worktree.Contains("base") ? Ran(0, 2) : Ran(2, 0));

        var oracle = new TaskOracle(Runner);
        var verdict = await oracle.VerifyAsync("/wt/base", "/wt/merge", "FullyQualifiedName~NewTest", CancellationToken.None);
        Assert.True(verdict.Verified, verdict.Reason);

        // A PR whose tests already pass on base is not an oracle.
        Task<TestRunOutcome> AlwaysGreen(string worktree, string filter, CancellationToken ct) => Task.FromResult(Ran(2, 0));
        var notOracle = await new TaskOracle(AlwaysGreen).VerifyAsync("/wt/base", "/wt/merge", "", CancellationToken.None);
        Assert.False(notOracle.Verified);
    }
}
