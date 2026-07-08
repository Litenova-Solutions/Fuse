using Fuse.Retrieval;
using Xunit;

namespace Fuse.Retrieval.Tests;

// U2: claims Fuse emits are graded, computed from the evidence available (never asserted). Graph-grade evidence
// caps at partially_verified (the grade-inflation guard); compiler/test evidence is verified. The renderer emits a
// scannable text block, empty when there are no claims.
public sealed class ClaimLedgerTests
{
    [Fact]
    public void Graph_evidence_caps_at_partially_verified()
    {
        var claim = Claim.FromGraph("WidgetService has 3 callers", "graph: references edges");
        Assert.Equal(ClaimGrade.PartiallyVerified, claim.Grade);
    }

    [Fact]
    public void Compiler_or_test_evidence_is_verified()
    {
        var claim = Claim.FromCompiler("the edit compiles clean", "check: 0 errors");
        Assert.Equal(ClaimGrade.Verified, claim.Grade);
    }

    [Fact]
    public void Render_lists_each_claim_with_its_grade_and_evidence()
    {
        var text = ClaimLedger.Render(
        [
            Claim.FromCompiler("the edit compiles clean", "check: 0 errors"),
            Claim.FromGraph("WidgetService is on the public surface", "symbol: WidgetService"),
            new Claim("the resolved handler moved", ClaimGrade.Contradicted, "session vs current"),
        ]);

        Assert.Contains("claims (3", text);
        Assert.Contains("[verified] the edit compiles clean  (evidence: check: 0 errors)", text);
        Assert.Contains("[partially verified] WidgetService is on the public surface", text);
        Assert.Contains("[contradicted] the resolved handler moved", text);
    }

    [Fact]
    public void Render_is_empty_with_no_claims()
    {
        Assert.Equal(string.Empty, ClaimLedger.Render([]));
    }
}
