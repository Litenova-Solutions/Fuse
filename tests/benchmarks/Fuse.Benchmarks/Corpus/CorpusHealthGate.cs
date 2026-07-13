using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;

namespace Fuse.Benchmarks;

/// <summary>
///     The decision of the corpus-health gate: whether a model-driven suite may run, and at what scope.
/// </summary>
/// <param name="Allowed">Whether the corpus is proven healthy enough to spend model time on.</param>
/// <param name="Reason">The actionable reason, named whether allowed or refused.</param>
/// <param name="ReducedScope">
///     When true the run is allowed only at reduced scope (the pre-registered C4/D20 fallback): the corpus is
///     below the full minimums but has at least <see cref="CorpusHealthReport.ReducedScopeTaskFloor" /> verified
///     tasks, so the run is a no-headline, CI-reported pilot and its result must be labeled as such.
/// </param>
public sealed record GateDecision(bool Allowed, string Reason, bool ReducedScope = false);

/// <summary>
///     Guards the model-driven suites (loop, agent) behind a fresh, passing corpus-health report (C4). A
///     model-driven run costs real model time, so it must not start unless the corpus is proven a usable arena:
///     a <c>corpus-health.json</c> that exists, is newer than the corpus manifest (so it reflects the current
///     corpus), and meets the minimums (at least 20 tier-1 repositories and 60 verified oracle tasks). This is
///     the enforcement that a 4.0-style null-by-environment cannot recur unnoticed: the suite refuses and names
///     the reason instead of silently scoring a corpus that does not build.
/// </summary>
public static class CorpusHealthGate
{
    /// <summary>
    ///     Decides whether a model-driven suite may run. A matching manifest fingerprint is authoritative;
    ///     timestamp comparison remains the compatibility path for reports produced before fingerprints shipped.
    /// </summary>
    /// <param name="report">The parsed corpus-health report, or null when the report is missing or unparseable.</param>
    /// <param name="reportGeneratedUtc">The report's generated timestamp, or null when absent or unparseable.</param>
    /// <param name="manifestModifiedUtc">The corpus manifest's last-write time (UTC).</param>
    /// <param name="manifestSha256">The current corpus manifest SHA-256, or null when it cannot be read.</param>
    /// <returns>The gate decision.</returns>
    public static GateDecision Evaluate(
        CorpusHealthReport? report,
        DateTime? reportGeneratedUtc,
        DateTime manifestModifiedUtc,
        string? manifestSha256 = null)
    {
        if (report is null)
            return new GateDecision(false, "no corpus-health.json; run `fuse eval corpus-health` first (a model-driven suite requires proof the corpus builds and has runnable tests).");
        var fingerprintMatches = report.ManifestSha256 is not null
            && manifestSha256 is not null
            && report.ManifestSha256.Equals(manifestSha256, StringComparison.OrdinalIgnoreCase);
        if (report.ManifestSha256 is not null && !fingerprintMatches)
            return new GateDecision(false, "corpus-health.json was produced from a different corpus manifest; re-run `fuse eval corpus-health` for the current manifest.");
        if (!fingerprintMatches && (reportGeneratedUtc is null || reportGeneratedUtc.Value < manifestModifiedUtc))
            return new GateDecision(false, "corpus-health.json is older than the corpus manifest; re-run `fuse eval corpus-health` so the health report reflects the current corpus.");
        if (report.MeetsMinimums)
            return new GateDecision(true, $"corpus healthy: {report.ReposTier1} tier-1 repos, {report.TasksVerified} verified oracle tasks.");
        // Below the full minimums: the pre-registered reduced-scope fallback (C4/D20) allows a no-headline pilot
        // when at least the reduced-scope task floor is verified, so the referendum can still run with CIs and a
        // labeled caveat rather than not at all. Below the floor there is no arena and the suite refuses.
        if (report.TasksVerified >= CorpusHealthReport.ReducedScopeTaskFloor)
            return new GateDecision(
                true,
                $"corpus below full minimums ({report.ReposTier1}/{report.MinReposTier1} tier-1 repos, {report.TasksVerified}/{report.MinTasksVerified} verified oracle tasks); running REDUCED-SCOPE (no headline, report with confidence intervals) per the pre-registered C4 fallback. The shortfall is an environment-buildability finding.",
                ReducedScope: true);
        return new GateDecision(false, $"corpus-health does not meet the minimums and is below the reduced-scope floor: {report.ReposTier1}/{report.MinReposTier1} tier-1 repos, {report.TasksVerified} verified oracle tasks (need at least {CorpusHealthReport.ReducedScopeTaskFloor} to run a reduced-scope pilot). Curate corpus v2 (C4) before a model-driven run.");
    }

    /// <summary>
    ///     Loads the report and manifest and evaluates the gate.
    /// </summary>
    /// <param name="benchRoot">The benchmark root (holds the default <c>corpus.json</c>).</param>
    /// <param name="resultsRoot">The results directory (holds <c>corpus-health.json</c>).</param>
    /// <param name="manifestPath">The corpus manifest used by the suite, or null for <c>corpus.json</c>.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The gate decision.</returns>
    public static async Task<GateDecision> CheckAsync(
        string benchRoot,
        string resultsRoot,
        string? manifestPath,
        CancellationToken cancellationToken)
    {
        var resolvedManifestPath = manifestPath ?? Path.Combine(benchRoot, "corpus.json");
        var manifestModifiedUtc = File.Exists(resolvedManifestPath)
            ? File.GetLastWriteTimeUtc(resolvedManifestPath)
            : DateTime.MinValue;
        string? manifestSha256 = null;
        if (File.Exists(resolvedManifestPath))
        {
            var bytes = await File.ReadAllBytesAsync(resolvedManifestPath, cancellationToken);
            manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        var reportPath = Path.Combine(resultsRoot, CorpusHealthReport.FileName);
        if (!File.Exists(reportPath))
            return Evaluate(null, null, manifestModifiedUtc, manifestSha256);

        CorpusHealthReport? report;
        try
        {
            var json = await File.ReadAllTextAsync(reportPath, cancellationToken);
            report = JsonSerializer.Deserialize(json, BenchmarkJsonContext.Default.CorpusHealthReport);
        }
        catch (JsonException)
        {
            return Evaluate(null, null, manifestModifiedUtc, manifestSha256);
        }

        DateTime? generatedUtc = null;
        if (report is not null && DateTime.TryParse(
                report.Generated, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            generatedUtc = parsed;

        return Evaluate(report, generatedUtc, manifestModifiedUtc, manifestSha256);
    }
}
