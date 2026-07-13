using Fuse.Benchmarks;
using Xunit;

namespace Fuse.Benchmarks.Tests;

// D22a: the loop oracle post-check decision. The gold-test run over the agent's edit is what turns the
// transcript reached-green proxy into a true pass@1 and exposes false-done; the decision logic is pure and
// unit-tested here (the git-checkout + dotnet-test plumbing is exercised by a provisioned run).
public sealed class OraclePostCheckTests
{
    [Fact]
    public void Green_gold_tests_are_a_true_pass_and_never_false_done()
    {
        var v = OraclePostCheck.Decide(proxyReachedGreen: true, new TestRunOutcome(Executed: true, Passed: 3, Failed: 0));

        Assert.True(v.OraclePassed);
        Assert.False(v.FalseDone);
    }

    [Fact]
    public void Proxy_green_but_red_gold_tests_is_false_done()
    {
        // The agent's transcript declared success, but the gold tests are red over its edit: the silent wrong
        // answer the token and turn metrics cannot see.
        var v = OraclePostCheck.Decide(proxyReachedGreen: true, new TestRunOutcome(Executed: true, Passed: 1, Failed: 2));

        Assert.False(v.OraclePassed);
        Assert.True(v.FalseDone);
        Assert.Contains("FALSE-DONE", v.Reason);
    }

    [Fact]
    public void Red_gold_tests_without_a_proxy_green_is_a_fail_but_not_false_done()
    {
        var v = OraclePostCheck.Decide(proxyReachedGreen: false, new TestRunOutcome(Executed: true, Passed: 0, Failed: 1));

        Assert.False(v.OraclePassed);
        Assert.False(v.FalseDone); // the agent did not claim green, so this is an honest miss
    }

    [Fact]
    public void A_gold_test_run_that_did_not_execute_is_not_scored()
    {
        var v = OraclePostCheck.Decide(proxyReachedGreen: true, TestRunOutcome.DidNotExecute);

        Assert.Null(v.OraclePassed); // a build failure in the gold tests is not a true-pass verdict either way
        Assert.False(v.FalseDone);
    }

    [Fact]
    public void No_gold_tests_persisted_leaves_the_oracle_unrun()
    {
        var v = OraclePostCheck.Decide(proxyReachedGreen: true, oracle: null);

        Assert.Null(v.OraclePassed);
        Assert.False(v.FalseDone);
        Assert.Contains("no gold tests", v.Reason);
    }
}
