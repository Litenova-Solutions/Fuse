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

    [Fact]
    public void Regrade_marks_a_claim_stale_when_its_evidence_changed()
    {
        var claim = Claim.FromCompiler("the edit compiles clean", "check: 0 errors");
        var reviewed = ClaimReviewer.Regrade(claim, evidenceChanged: true);
        Assert.Equal(ClaimGrade.Stale, reviewed.Grade);
        Assert.Contains("stale", reviewed.Evidence);
    }

    [Fact]
    public void Regrade_leaves_a_claim_unchanged_when_its_evidence_is_unchanged()
    {
        var claim = Claim.FromGraph("3 callers", "graph: references edges");
        var reviewed = ClaimReviewer.Regrade(claim, evidenceChanged: false);
        Assert.Equal(ClaimGrade.PartiallyVerified, reviewed.Grade);
        Assert.Equal(claim, reviewed);
    }

    [Fact]
    public void Regrade_does_not_revert_a_terminal_grade()
    {
        var contradicted = Claim.Contradicted("the handler is X", "X", "Y");
        Assert.Equal(ClaimGrade.Contradicted, ClaimReviewer.Regrade(contradicted, evidenceChanged: true).Grade);
    }

    [Fact]
    public void Contradicted_cites_both_sides()
    {
        var claim = Claim.Contradicted("the request resolves to OldHandler", "OldHandler", "NewHandler");
        Assert.Equal(ClaimGrade.Contradicted, claim.Grade);
        Assert.Contains("was: OldHandler", claim.Evidence);
        Assert.Contains("now: NewHandler", claim.Evidence);
    }
}
