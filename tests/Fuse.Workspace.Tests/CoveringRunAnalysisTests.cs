using Fuse.Workspace;
using Xunit;

namespace Fuse.Workspace.Tests;

// T1: a covering type that the filter requested but that produced no verdict is reported not-runnable, never
// counted as passed. A type with at least one verdict (even a failing one) is runnable.
public sealed class CoveringRunAnalysisTests
{
    private static TestVerdict Verdict(string name, string outcome = "passed") => new(name, outcome);

    [Fact]
    public void A_covering_type_with_no_verdict_is_not_runnable()
    {
        var notRunnable = CoveringRunAnalysis.NotRunnableTypes(
            ["Ns.RanTests", "Ns.NeverRanTests"],
            [Verdict("Ns.RanTests.Case1"), Verdict("Ns.RanTests.Case2")]);

        Assert.Equal(["Ns.NeverRanTests"], notRunnable);
    }

    [Fact]
    public void A_covering_type_with_a_failing_verdict_is_runnable()
    {
        var notRunnable = CoveringRunAnalysis.NotRunnableTypes(
            ["Ns.RanTests"],
            [Verdict("Ns.RanTests.Case1", "failed")]);

        Assert.Empty(notRunnable);
    }

    [Fact]
    public void All_covering_types_absent_from_the_results_are_reported()
    {
        var notRunnable = CoveringRunAnalysis.NotRunnableTypes(["Ns.A", "Ns.B"], []);
        Assert.Equal(["Ns.A", "Ns.B"], notRunnable);
    }
}
