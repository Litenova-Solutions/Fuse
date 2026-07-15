namespace Fuse.Benchmarks;

/// <summary>
///     Per-operation latency and self-time metrics for the R8 index hot-path profile.
/// </summary>
/// <param name="P50Ms">The P50 wall-clock latency in milliseconds.</param>
/// <param name="P95Ms">The P95 wall-clock latency in milliseconds.</param>
/// <param name="SelfTimePercent">The share of stack samples attributed to SQLite or Roslyn frames.</param>
/// <param name="SampleCount">The number of timed iterations.</param>
public sealed record ProfileV42OperationMetrics(
    double P50Ms,
    double P95Ms,
    double SelfTimePercent,
    int SampleCount);

/// <summary>
///     One attributed frame in the R8 profile hotspot tables.
/// </summary>
/// <param name="Frame">The declaring type and method name.</param>
/// <param name="SelfTimePercent">The frame's share of samples in its category.</param>
/// <param name="Samples">The raw sample count.</param>
public sealed record ProfileV42HotspotFrame(
    string Frame,
    double SelfTimePercent,
    int Samples);

/// <summary>
///     SQL and Roslyn hotspot tables for store-split ordering (R8/R9).
/// </summary>
/// <param name="Sql">Top SQLite and <c>Fuse.Indexing</c> frames.</param>
/// <param name="Roslyn">Top Roslyn and <c>Fuse.Semantics</c> frames.</param>
public sealed record ProfileV42Hotspots(
    IReadOnlyList<ProfileV42HotspotFrame> Sql,
    IReadOnlyList<ProfileV42HotspotFrame> Roslyn);

/// <summary>
///     The four warm index verbs profiled by the R8 harness.
/// </summary>
/// <param name="Localize">Open-ended localization over the warm store.</param>
/// <param name="FindSymbol">Exact symbol lookup.</param>
/// <param name="ReviewPlan">Review planning against a git base.</param>
/// <param name="Reconcile">Single-file reconcile (re-index one dirty file).</param>
public sealed record ProfileV42Operations(
    ProfileV42OperationMetrics Localize,
    ProfileV42OperationMetrics FindSymbol,
    ProfileV42OperationMetrics ReviewPlan,
    ProfileV42OperationMetrics Reconcile);

/// <summary>
///     The R8 index hot-path profile artifact written to <see cref="FileName" />.
/// </summary>
/// <param name="SchemaVersion">The on-disk schema version.</param>
/// <param name="FuseVersion">The producing Fuse product version.</param>
/// <param name="Suite">The suite name (<c>profile-v42</c>).</param>
/// <param name="Description">A one-line description of what the artifact records.</param>
/// <param name="Generated">The ISO-8601 UTC time the run was produced, or null when not stamped.</param>
/// <param name="Placeholder">Whether the artifact carries placeholder timings.</param>
/// <param name="Repo">The corpus repository profiled.</param>
/// <param name="IndexMode">The achieved index mode after the cold index pass.</param>
/// <param name="FileCount">The indexed file count.</param>
/// <param name="SymbolCount">The indexed symbol count.</param>
/// <param name="Iterations">The timed iteration count per verb.</param>
/// <param name="Operations">The per-verb latency and self-time metrics.</param>
/// <param name="Hotspots">The SQL and Roslyn frame attribution tables.</param>
/// <param name="Notes">Methodology and environment notes.</param>
public sealed record ProfileV42Report(
    int SchemaVersion,
    string FuseVersion,
    string Suite,
    string Description,
    string? Generated,
    bool Placeholder,
    string Repo,
    string IndexMode,
    int FileCount,
    int SymbolCount,
    int Iterations,
    ProfileV42Operations Operations,
    ProfileV42Hotspots Hotspots,
    IReadOnlyList<string> Notes)
{
    /// <summary>The committed artifact file name under <c>tests/benchmarks/results</c>.</summary>
    public const string FileName = "profile-v42.json";

    /// <summary>The on-disk schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The default timed iteration count per verb.</summary>
    public const int DefaultIterations = 25;
}
