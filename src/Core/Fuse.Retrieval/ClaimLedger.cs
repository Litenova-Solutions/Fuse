namespace Fuse.Retrieval;

/// <summary>
///     The grade of a claim Fuse emits in an answer (U2, the metrics dictionary). Grades are computed from the
///     evidence available, never asserted, so an agent and its human know which statements are compiler- or
///     test-grade truth and which are weaker inferences that may go stale or be contradicted.
/// </summary>
public enum ClaimGrade
{
    /// <summary>Backed by compiler- or test-grade evidence (a diagnostic, a build, a test verdict): the strongest grade.</summary>
    Verified,

    /// <summary>Backed by the persisted graph only (an edge, a stored flag): real signal, but not compiler-confirmed; the graph-grade cap.</summary>
    PartiallyVerified,

    /// <summary>The evidence a claim rested on has changed since the claim was computed (a watcher-known edit to the evidence file).</summary>
    Stale,

    /// <summary>A claim made earlier in a session conflicts with the current truth; both sides are cited.</summary>
    Contradicted,
}

/// <summary>
///     One graded claim in an answer (U2): a statement Fuse computed, its grade, and a reference to the evidence
///     that grade rests on. Grades attach only to statements Fuse emitted, never to prose a model wrote.
/// </summary>
/// <param name="Statement">The claim, as a short sentence Fuse computed.</param>
/// <param name="Grade">The computed grade.</param>
/// <param name="Evidence">A reference to the evidence (a file:line, a diagnostic id, a symbol id, a test id).</param>
public sealed record Claim(string Statement, ClaimGrade Grade, string Evidence)
{
    /// <summary>
    ///     Creates a claim from graph-grade evidence, capped at <see cref="ClaimGrade.PartiallyVerified" /> because
    ///     the persisted graph is real signal but not compiler-confirmed (the grade-inflation kill-risk mitigation).
    /// </summary>
    /// <param name="statement">The claim.</param>
    /// <param name="evidence">The graph evidence reference.</param>
    /// <returns>A partially-verified claim.</returns>
    public static Claim FromGraph(string statement, string evidence) => new(statement, ClaimGrade.PartiallyVerified, evidence);

    /// <summary>
    ///     Creates a claim from compiler- or test-grade evidence (a diagnostic, a build, a test verdict), graded
    ///     <see cref="ClaimGrade.Verified" />.
    /// </summary>
    /// <param name="statement">The claim.</param>
    /// <param name="evidence">The compiler/test evidence reference.</param>
    /// <returns>A verified claim.</returns>
    public static Claim FromCompiler(string statement, string evidence) => new(statement, ClaimGrade.Verified, evidence);
}

/// <summary>
///     Renders a set of graded claims as the text block appended to an answer (U2). The block is a plain,
///     scannable section - one line per claim with its grade and evidence - matching the availability-header and
///     API-surface lines the read tools already emit, since the tools return rendered strings rather than a
///     structured envelope.
/// </summary>
public static class ClaimLedger
{
    /// <summary>
    ///     Renders the claims block, or an empty string when there are no claims.
    /// </summary>
    /// <param name="claims">The graded claims.</param>
    /// <returns>The rendered block (a header plus one line per claim), or empty when there are none.</returns>
    public static string Render(IReadOnlyList<Claim> claims)
    {
        if (claims.Count == 0)
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"claims ({claims.Count}, each graded and evidence-referenced):");
        foreach (var claim in claims)
            builder.AppendLine($"  [{GradeLabel(claim.Grade)}] {claim.Statement}  (evidence: {claim.Evidence})");
        return builder.ToString().TrimEnd();
    }

    private static string GradeLabel(ClaimGrade grade) => grade switch
    {
        ClaimGrade.Verified => "verified",
        ClaimGrade.PartiallyVerified => "partially verified",
        ClaimGrade.Stale => "stale",
        ClaimGrade.Contradicted => "contradicted",
        _ => "unknown",
    };
}
