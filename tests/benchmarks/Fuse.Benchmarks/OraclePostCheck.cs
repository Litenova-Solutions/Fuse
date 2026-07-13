namespace Fuse.Benchmarks;

/// <summary>
///     The verdict of the loop oracle post-check (D22a): whether an agent's finished edit actually makes the
///     task's fail-to-pass tests pass, computed by re-running the gold tests against the agent's working tree
///     rather than trusting the transcript. This is what turns the loop suite's transcript-derived reached-green
///     proxy into a true pass@1, and it exposes false-done (the agent declared success but the tests still fail).
/// </summary>
/// <param name="OraclePassed">
///     True when the gold tests executed and were green over the agent's edit; false when they ran and failed;
///     null when the oracle could not run (no gold tests persisted for the task, or the run did not execute), so
///     the rollout is not scored for true pass rather than counted as a failure.
/// </param>
/// <param name="FalseDone">
///     True when the transcript proxy said the agent reached green but the oracle says it did not: a silent wrong
///     answer the token and turn metrics cannot see. Only meaningful when <see cref="OraclePassed" /> is non-null.
/// </param>
/// <param name="Reason">A short explanation of the verdict.</param>
public sealed record OracleVerdictResult(bool? OraclePassed, bool FalseDone, string Reason);

/// <summary>
///     Decides the loop oracle post-check verdict from the transcript proxy and the gold-test run (D22a). Pure and
///     unit-tested; the suite supplies the actual test-run outcome by checking out the task's gold test files onto
///     the agent's edited worktree and running them.
/// </summary>
public static class OraclePostCheck
{
    /// <summary>
    ///     Decides the verdict from the proxy reached-green and the gold-test run over the agent's edit.
    /// </summary>
    /// <param name="proxyReachedGreen">The transcript-derived reached-green proxy for this rollout.</param>
    /// <param name="oracle">
    ///     The gold-test run outcome over the agent's edited worktree, or null when the oracle could not be run
    ///     (the task has no persisted gold tests, so it is not scorable for true pass).
    /// </param>
    /// <returns>The verdict: true pass, false-done, and a reason.</returns>
    public static OracleVerdictResult Decide(bool proxyReachedGreen, TestRunOutcome? oracle)
    {
        if (oracle is null)
            return new OracleVerdictResult(null, false, "oracle not run (no gold tests persisted for the task)");

        if (!oracle.Executed)
            return new OracleVerdictResult(null, false, "gold tests did not execute over the edit (build failure or runner error); not scored for true pass");

        var passed = oracle.IsGreen;
        // False-done is the honest failure the proxy hides: the agent's transcript claimed green, but the gold
        // tests are red over its edit. It is only meaningful when the oracle actually ran and executed.
        var falseDone = proxyReachedGreen && !passed;
        var reason = passed
            ? $"oracle green ({oracle.Passed} passed, {oracle.Failed} failed)"
            : $"oracle red ({oracle.Passed} passed, {oracle.Failed} failed){(falseDone ? "; FALSE-DONE (proxy said green)" : string.Empty)}";
        return new OracleVerdictResult(passed, falseDone, reason);
    }
}
