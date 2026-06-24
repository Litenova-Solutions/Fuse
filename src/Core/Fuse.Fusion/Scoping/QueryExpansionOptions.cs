namespace Fuse.Fusion.Scoping;

/// <summary>
///     Controls pseudo-relevance feedback query expansion for BM25F query scoping.
/// </summary>
/// <param name="FeedbackDocs">
///     The number of top-ranked files from the first pass treated as pseudo-relevant feedback. Their declared
///     symbol names are the source of candidate expansion terms.
/// </param>
/// <param name="ExpansionTerms">The maximum number of expansion terms appended to the original query.</param>
/// <param name="MinFeedbackDocs">
///     The minimum number of feedback files a candidate term must appear in to be admitted. Requiring a term
///     to recur across several top hits filters incidental names from a single document, the main failure
///     mode of feedback expansion on a weak first pass.
/// </param>
/// <param name="ExpansionWeight">
///     The per-term weight assigned to each expansion term, relative to the original query terms (weight
///     <c>1.0</c>). Kept low so expansion nudges ranking toward co-occurring concepts without overriding the
///     original intent; a heavier weight lifts a few extra files but also displaces incidental first-pass hits
///     when a query's title is poorly aligned with its change, which costs more recall than it gains.
/// </param>
/// <param name="MinInitialHits">
///     The minimum number of files the first pass must match before expansion runs. Below this the first
///     ranking is too sparse to trust as feedback, so the original query is used unchanged.
/// </param>
/// <param name="MinExpansionIdf">
///     The minimum corpus inverse document frequency a candidate term must clear to be admitted, and the
///     weight by which candidates are ranked (feedback weight times IDF). It suppresses corpus-wide
///     boilerplate symbol names (for example a type suffix shared by most files), the dominant way
///     pseudo-relevance feedback broadens a weak first pass into irrelevance.
/// </param>
/// <remarks>
///     Expansion is a fast, fully lexical second pass: it harvests recurring declared-symbol terms from the
///     first pass's top files and re-ranks with them blended in at a reduced weight. It runs on the default
///     scoping path and performs no model inference or network access. <see cref="Disabled" /> turns it off.
/// </remarks>
public sealed record QueryExpansionOptions(
    int FeedbackDocs = 5,
    int ExpansionTerms = 8,
    int MinFeedbackDocs = 2,
    double ExpansionWeight = 0.2,
    int MinInitialHits = 3,
    double MinExpansionIdf = 1.0)
{
    /// <summary>A configuration that performs no expansion (zero expansion terms).</summary>
    public static QueryExpansionOptions Disabled { get; } = new(ExpansionTerms: 0);

    /// <summary>Whether expansion is active under this configuration.</summary>
    public bool Enabled => ExpansionTerms > 0 && ExpansionWeight > 0 && FeedbackDocs > 0;
}
