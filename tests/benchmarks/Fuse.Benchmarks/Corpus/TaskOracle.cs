using System.Text.RegularExpressions;

namespace Fuse.Benchmarks;

/// <summary>
///     The outcome of running a task's tests at one commit: whether the tests compiled and executed, and the
///     pass and fail counts parsed from the test runner output. When <see cref="Executed" /> is false the tests
///     did not run (a build failure or a runner error), which on the base commit counts as the tests failing
///     (the new tests reference code that does not exist yet).
/// </summary>
/// <param name="Executed">Whether the test run compiled and executed (regardless of pass or fail).</param>
/// <param name="Passed">The number of tests that passed.</param>
/// <param name="Failed">The number of tests that failed.</param>
public sealed record TestRunOutcome(bool Executed, int Passed, int Failed)
{
    /// <summary>A run that did not compile or execute (a build failure or runner error).</summary>
    public static readonly TestRunOutcome DidNotExecute = new(false, 0, 0);

    /// <summary>Whether this run is green: it executed, at least one test passed, and none failed.</summary>
    public bool IsGreen => Executed && Failed == 0 && Passed > 0;

    /// <summary>Whether this run shows the tests failing: it did not execute, or at least one test failed.</summary>
    public bool ShowsFailure => !Executed || Failed > 0;
}

/// <summary>
///     The verdict of oracle verification for a candidate task (C4): whether the new or changed tests fail on the
///     base commit and pass on the merge commit, and are not flaky.
/// </summary>
/// <param name="Verified">Whether the task is a usable oracle: fail-on-base, pass-on-merge, not flaky.</param>
/// <param name="Reason">A short explanation of the verdict.</param>
public sealed record OracleVerdict(bool Verified, string Reason);

/// <summary>
///     Verifies a candidate benchmark task's test oracle (C4): the mechanical proof that a task is scorable by
///     tests, not just by changed-file overlap. A task qualifies when the PR's new or changed tests FAIL on the
///     base commit and PASS on the merge commit; a task whose merge run is flaky (a re-run disagrees) is
///     excluded. This turns a PR into a fail-to-pass oracle a model-driven suite can score green against.
/// </summary>
public sealed class TaskOracle
{
    private readonly Func<string, string, CancellationToken, Task<TestRunOutcome>> _runTests;

    /// <summary>
    ///     Initializes a new instance of the <see cref="TaskOracle" /> class.
    /// </summary>
    /// <param name="runTests">
    ///     A callback that runs the task's tests at a worktree path with a test filter and returns the outcome.
    ///     Injected so the decision logic can be tested without executing a real suite.
    /// </param>
    public TaskOracle(Func<string, string, CancellationToken, Task<TestRunOutcome>> runTests) => _runTests = runTests;

    /// <summary>
    ///     Decides whether a task is a usable oracle from its runs. Pure: fail-on-base, green-on-merge, and a
    ///     merge re-run that agrees (not flaky).
    /// </summary>
    /// <param name="onBase">The test run at the base commit (with the PR's test changes applied).</param>
    /// <param name="onMerge">The test run at the merge commit.</param>
    /// <param name="onMergeRerun">A second test run at the merge commit, to detect flakiness.</param>
    /// <returns>The verdict.</returns>
    public static OracleVerdict Decide(TestRunOutcome onBase, TestRunOutcome onMerge, TestRunOutcome onMergeRerun)
    {
        if (!onMerge.IsGreen)
            return new OracleVerdict(false, $"merge run not green (executed {onMerge.Executed}, passed {onMerge.Passed}, failed {onMerge.Failed})");
        if (!onBase.ShowsFailure)
            return new OracleVerdict(false, $"base run did not fail (passed {onBase.Passed}, failed {onBase.Failed}); the tests do not distinguish base from merge");
        if (onMergeRerun.Passed != onMerge.Passed || onMergeRerun.Failed != onMerge.Failed)
            return new OracleVerdict(false, $"merge run is flaky (re-run passed {onMergeRerun.Passed}/failed {onMergeRerun.Failed} vs {onMerge.Passed}/{onMerge.Failed})");
        return new OracleVerdict(true, $"fail-to-pass: base fails, merge green ({onMerge.Passed} passed), re-run agrees");
    }

    /// <summary>
    ///     Verifies a task by running its tests at the base and merge worktrees. The caller supplies both
    ///     worktree paths (already checked out with the PR's test changes present) and a test filter.
    /// </summary>
    /// <param name="baseWorktree">The base-commit worktree path, with the PR's test changes applied.</param>
    /// <param name="mergeWorktree">The merge-commit worktree path.</param>
    /// <param name="testFilter">The test filter selecting the PR's new or changed tests.</param>
    /// <param name="cancellationToken">A token to cancel the verification.</param>
    /// <returns>The oracle verdict.</returns>
    public async Task<OracleVerdict> VerifyAsync(
        string baseWorktree, string mergeWorktree, string testFilter, CancellationToken cancellationToken)
    {
        var onBase = await _runTests(baseWorktree, testFilter, cancellationToken);
        var onMerge = await _runTests(mergeWorktree, testFilter, cancellationToken);
        var onMergeRerun = onMerge.IsGreen
            ? await _runTests(mergeWorktree, testFilter, cancellationToken)
            : onMerge; // No need to re-run when the first merge run was not green; Decide will reject it.
        return Decide(onBase, onMerge, onMergeRerun);
    }

    /// <summary>
    ///     Runs the tests in a worktree with a filter via <c>dotnet test</c> and parses the pass and fail counts
    ///     from the runner output. A build or runner failure returns <see cref="TestRunOutcome.DidNotExecute" />.
    /// </summary>
    /// <param name="worktree">The worktree to run tests in.</param>
    /// <param name="testFilter">The <c>--filter</c> expression, or empty to run all discovered tests.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>The parsed outcome.</returns>
    public static async Task<TestRunOutcome> RunDotnetTestAsync(string worktree, string testFilter, CancellationToken cancellationToken)
    {
        var args = string.IsNullOrWhiteSpace(testFilter)
            ? new[] { "test", "--nologo", "-v:quiet" }
            : ["test", "--nologo", "-v:quiet", "--filter", testFilter];
        var result = await DotnetCli.RunAsync(worktree, cancellationToken, args);
        return ParseTestOutput(result.StdOut + "\n" + result.StdErr, result.ExitCode);
    }

    // Parses the VSTest summary lines ("Passed: N", "Failed: N") from the runner output. When no summary is
    // present (a build failure produces none), the run did not execute.
    internal static TestRunOutcome ParseTestOutput(string output, int exitCode)
    {
        var passed = SumMatches(output, @"Passed:\s+(\d+)");
        var failed = SumMatches(output, @"Failed:\s+(\d+)");
        var hasSummary = Regex.IsMatch(output, @"(Passed|Failed)!\s") || passed > 0 || failed > 0;
        if (!hasSummary)
            return TestRunOutcome.DidNotExecute; // No test summary emitted: the build failed or no tests ran.
        return new TestRunOutcome(Executed: true, passed, failed);
    }

    private static int SumMatches(string text, string pattern)
    {
        var total = 0;
        foreach (Match m in Regex.Matches(text, pattern))
        {
            if (int.TryParse(m.Groups[1].Value, out var n))
                total += n;
        }

        return total;
    }
}
