using Fuse.Indexing;

namespace Fuse.Workspace;

/// <summary>
///     One candidate changeset in a race (F2): a proposed single-file edit, identified so the per-candidate
///     verdict can be attributed back to it. The edit is the shipped <c>fuse_check</c> contract (a file path and
///     its proposed full new content), so a candidate is exactly what a single speculative check verifies.
/// </summary>
/// <param name="Id">The caller's label for this candidate (echoed in the verdict; defaults to its index when blank).</param>
/// <param name="File">The repo-relative path of the file the candidate changes.</param>
/// <param name="Content">The proposed full new content of that file.</param>
public sealed record RaceCandidate(string Id, string File, string Content);

/// <summary>
///     One candidate's verdict in a race (F2): the diagnostics its speculative typecheck produced, whether the
///     check was applicable (the file was part of a held compilation), and the derived clean/red status.
/// </summary>
/// <param name="Id">The candidate's label.</param>
/// <param name="File">The candidate's changed file.</param>
/// <param name="Applicable">
///     True when a resident compilation contained the file and answered; false when no held compilation covers it
///     (the overlay check returned null), so the candidate is neither clean nor red but not-applicable.
/// </param>
/// <param name="Diagnostics">The changed document's diagnostics (empty when clean or not applicable).</param>
public sealed record RaceVerdict(
    string Id, string File, bool Applicable, IReadOnlyList<CheckDiagnostic> Diagnostics)
{
    /// <summary>The number of error-severity diagnostics the candidate produced.</summary>
    public int ErrorCount => Diagnostics.Count(d => d.Severity == "Error");

    /// <summary>The number of warning-severity diagnostics the candidate produced.</summary>
    public int WarningCount => Diagnostics.Count(d => d.Severity == "Warning");

    /// <summary>
    ///     True when the candidate applied and produced no error-severity diagnostic (warnings do not disqualify a
    ///     candidate from being clean; only errors are a red verdict).
    /// </summary>
    public bool IsClean => Applicable && ErrorCount == 0;
}

/// <summary>
///     The outcome of racing k candidate changesets (F2): every candidate's verdict plus a winner suggested by
///     strict dominance only. A winner exists only when exactly one candidate is clean and at least one other is
///     not; two or more clean candidates are a tie (strict dominance cannot choose a green over a green); zero
///     clean candidates leave no winner. The caller renders this; no candidate is applied.
/// </summary>
/// <param name="Verdicts">The per-candidate verdicts, in the input order.</param>
/// <param name="WinnerId">The uniquely-dominant candidate's id, or null when there is a tie or no clean candidate.</param>
/// <param name="Tie">True when two or more candidates are equally clean, so no single winner is suggested.</param>
public sealed record RaceReport(IReadOnlyList<RaceVerdict> Verdicts, string? WinnerId, bool Tie)
{
    /// <summary>The candidates that applied and are error-free.</summary>
    public IReadOnlyList<RaceVerdict> Clean => Verdicts.Where(v => v.IsClean).ToList();
}

/// <summary>
///     Races k candidate single-file edits through the speculative overlay typecheck (F2, candidate racing). Each
///     candidate forks the same held resident compilation (immutable snapshots share their unchanged syntax trees,
///     so a fork is cheap by construction and k checks cost far less than k cold loads), verifies it, and the
///     racer adjudicates a winner by strict dominance.
/// </summary>
/// <remarks>
///     <para>
///         Candidates are evaluated one at a time (sequentially), holding a single fork live at any moment. This
///         is F2's named Fallback, taken on a recorded measurement: on a 20-core host, racing three candidates
///         concurrently was within three percent of evaluating them sequentially (race/seq 1.03x), because
///         Roslyn's semantic binding over forks that share a base compilation serializes on the base's internal
///         binding caches rather than parallelizing. Concurrency therefore buys no wall-clock while multiplying the
///         live-fork memory by k (the item's stated kill risk), so the sequential evaluation is the honest ship:
///         same API and verdicts, peak memory bounded to one fork regardless of k. The fork-sharing that matters
///         (k checks costing far less than k cold rehydrations) is preserved; only the pointless concurrency is
///         dropped.
///     </para>
///     <para>
///         The racer verifies the fork-cheap primitive only - the speculative typecheck - because that is the
///         verification that runs at sampling speed. Per-candidate test execution over an unwritten candidate
///         needs the emit path (T1's descoped follow-up), so it is not part of a race; a candidate's covering
///         tests are run at build grade against the winner after it is applied, through the existing test verb.
///     </para>
///     <para>
///         The winner is suggested by strict dominance only: an all-clean candidate beats any candidate with an
///         error, ties among clean candidates are reported as ties, and nothing is chosen when no candidate is
///         clean. The racer never applies a candidate; it returns verdicts for the caller to act on.
///     </para>
/// </remarks>
public static class CandidateRacer
{
    /// <summary>
    ///     Evaluates the candidates through <paramref name="check" /> one at a time (holding one fork live at a
    ///     time) and returns each candidate's verdict plus a strict-dominance winner.
    /// </summary>
    /// <param name="check">
    ///     The overlay typecheck for one candidate: returns the changed document's diagnostics, or null when no
    ///     held compilation covers the file (reported as not-applicable). Invoked once per candidate, in order.
    /// </param>
    /// <param name="candidates">The candidate changesets to race (order is preserved in the report).</param>
    /// <param name="cancellationToken">A token to cancel the race.</param>
    /// <returns>The race report: per-candidate verdicts and the suggested winner (or a tie / no winner).</returns>
    /// <exception cref="ArgumentException">No candidates were supplied.</exception>
    public static async Task<RaceReport> RaceAsync(
        Func<RaceCandidate, CancellationToken, Task<IReadOnlyList<CheckDiagnostic>?>> check,
        IReadOnlyList<RaceCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new ArgumentException("At least one candidate is required to race.", nameof(candidates));

        // Evaluate candidates in order, one fork at a time: each candidate's fork rebinds only its changed tree
        // over the shared base (unchanged trees and metadata reused), and the previous fork is released before the
        // next is taken, so peak memory is a single overlay regardless of k.
        var verdicts = new List<RaceVerdict>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = await check(candidate, cancellationToken).ConfigureAwait(false);
            verdicts.Add(diagnostics is null
                ? new RaceVerdict(candidate.Id, candidate.File, Applicable: false, [])
                : new RaceVerdict(candidate.Id, candidate.File, Applicable: true, diagnostics));
        }

        return Adjudicate(verdicts);
    }

    // Strict dominance: a winner is named only when exactly one candidate is clean and at least one other is not
    // (a green strictly beats a red). Two or more clean candidates tie; zero clean candidates leave no winner.
    private static RaceReport Adjudicate(IReadOnlyList<RaceVerdict> verdicts)
    {
        var clean = verdicts.Where(v => v.IsClean).ToList();
        var anyNotClean = verdicts.Any(v => !v.IsClean);

        if (clean.Count == 1 && anyNotClean)
            return new RaceReport(verdicts, clean[0].Id, Tie: false);

        // Two or more equally-clean candidates are a tie; strict dominance cannot choose among greens.
        var tie = clean.Count >= 2;
        return new RaceReport(verdicts, WinnerId: null, tie);
    }
}
