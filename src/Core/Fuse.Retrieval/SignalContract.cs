namespace Fuse.Retrieval;

/// <summary>
///     The graded outcome of a localization request: how confident the engine is that it found the right
///     files. Because Fuse runs as an MCP server in a loop with a model, a low-signal request is answered by
///     refusing and routing (handing back a navigation map and asking for an anchor) rather than by returning
///     a low-precision guess.
/// </summary>
public enum SignalState
{
    /// <summary>
    ///     A candidate or a tight cluster stands clear of the rest. The engine returns that tight set; this is
    ///     where precision is won.
    /// </summary>
    Confident,

    /// <summary>
    ///     There is some signal but no clear winner. The engine returns a small best-effort set, flagged
    ///     low-confidence, together with a navigation map of refinement options.
    /// </summary>
    Partial,

    /// <summary>
    ///     No usable anchor (a no-signal title, or a near-empty, near-uniform score distribution). The engine
    ///     refuses and routes: it returns the navigation map it did see and asks for a sharper input.
    /// </summary>
    Insufficient,
}

/// <summary>
///     The navigation map handed back when a localization request is not confident. It turns a refusal into a
///     navigation step: an exploring agent that does not yet know the right symbol uses the map to find its
///     next probe. Built from the language-agnostic symbol, route, and file tables, so it carries to any
///     indexed language.
/// </summary>
/// <param name="CandidateAreas">The top areas (namespaces or folders) the request brushed against, most relevant first.</param>
/// <param name="EntryPoints">Entry-point files (for example a program, startup, or main file) and any routes, to orient exploration.</param>
/// <param name="NearestSymbols">The nearest indexed symbols to the request's terms, even when they scored below the candidate cutoff.</param>
/// <param name="Ask">An explicit, human-readable ask for a sharper input (a symbol, route, service, request, config section, git base, or narrower description).</param>
public sealed record NavigationMap(
    IReadOnlyList<string> CandidateAreas,
    IReadOnlyList<string> EntryPoints,
    IReadOnlyList<string> NearestSymbols,
    string Ask);

/// <summary>
///     Grades a scored candidate set into a <see cref="SignalState" /> from the score distribution alone, with
///     no model. The thresholds are fixed constants, so the grade is reproducible for a given set of scores.
/// </summary>
/// <remarks>
///     The rule is deliberately conservative on the insufficient side: a request that matches anything through
///     full-text retrieval clears the insufficient floor and lands in confident or partial, so an answerable
///     query is rarely refused (the false-rejection error the contract must keep low). Only a near-empty or
///     uniformly weak distribution is judged insufficient by score.
///     <para>
///     The floor (<see cref="InsufficientCeiling" />) was lowered from 0.30 to 0.20 when the dense embedding
///     channel was retired (item K1). Dense candidates carried a 0.72 base weight, so an answerable but
///     lexically weak query used to clear the old floor on its dense match; without that channel the same
///     queries score lower, and the 0.30 floor re-introduced false rejections (the lexical-only A/B recorded
///     3 of 52). The genuine no-signal case is caught upstream by <see cref="QuerySignalClassifier" />, so the
///     score floor only needs to reject a near-empty lexical distribution, which 0.20 still does.
///     </para>
/// </remarks>
public static class SignalGrader
{
    /// <summary>The minimum top score for a confident verdict. A single full-text body match (0.55) qualifies.</summary>
    public const double ConfidentScore = 0.55;

    /// <summary>The leading group is the candidates within this distance of the top score; the rest is the tail.</summary>
    public const double ClearGap = 0.15;

    /// <summary>The leading group must be at most this large to count as a tight, confident set.</summary>
    public const int ConfidentClusterMax = 3;

    /// <summary>Below this top score the distribution is too weak to anchor an answer; the verdict is insufficient.</summary>
    public const double InsufficientCeiling = 0.20;

    /// <summary>
    ///     Grades a ranked candidate set (highest score first) into a signal state.
    /// </summary>
    /// <param name="ranked">The scored candidates, ordered by score descending.</param>
    /// <returns>The graded state for the distribution.</returns>
    /// <remarks>
    ///     Confident requires a strong top score and a small leading group that stands clear of a tail: a single
    ///     candidate, or a tight group separated from the rest by at least <see cref="ClearGap" />. A flat run of
    ///     similar scores (no tail below the leading group) is partial, not confident, so an ambiguous distribution
    ///     never passes as a precise answer.
    /// </remarks>
    public static SignalState Grade(IReadOnlyList<ScoredCandidate> ranked)
    {
        if (ranked.Count == 0)
            return SignalState.Insufficient;

        var top = ranked[0].Score;
        if (top < InsufficientCeiling)
            return SignalState.Insufficient;
        if (top < ConfidentScore)
            return SignalState.Partial;
        if (ranked.Count == 1)
            return SignalState.Confident;

        var leading = LeadingCluster(ranked).Count;
        // A small leading group with a tail below it stands clear; a leading group that is the whole set (no tail)
        // is a flat distribution, which is ambiguous.
        return leading <= ConfidentClusterMax && leading < ranked.Count
            ? SignalState.Confident
            : SignalState.Partial;
    }

    /// <summary>
    ///     Returns the leading group: the top candidate plus any others within <see cref="ClearGap" /> of it. This
    ///     is the tight set returned on a confident verdict.
    /// </summary>
    /// <param name="ranked">The scored candidates, ordered by score descending.</param>
    /// <returns>The leading group, or an empty list when <paramref name="ranked" /> is empty.</returns>
    public static IReadOnlyList<ScoredCandidate> LeadingCluster(IReadOnlyList<ScoredCandidate> ranked)
    {
        if (ranked.Count == 0)
            return [];

        var floor = ranked[0].Score - ClearGap;
        return ranked.TakeWhile(c => c.Score >= floor).ToList();
    }
}
