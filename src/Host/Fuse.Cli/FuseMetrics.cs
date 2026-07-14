using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Fuse.Indexing;

namespace Fuse.Cli;

/// <summary>
///     Opt-in MCP serve metrics (F-017) via <see cref="System.Diagnostics.Metrics" />. Enabled when
///     <c>FUSE_METRICS=1</c> (or <c>true</c>/<c>yes</c>/<c>on</c>). No OpenTelemetry dependency is required;
///     attach a <see cref="MeterListener" /> or use <c>dotnet-counters</c> to export.
/// </summary>
public static class FuseMetrics
{
    private static readonly ConcurrentDictionary<string, string> IndexModesByRoot = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Histogram<double>? ToolDuration;
    private static readonly Counter<long>? ReconcileStamped;
    private static readonly Counter<long>? DegradedStates;

    // Always tracked (independent of the opt-in Meter) so a degraded state is queryable and testable even when
    // FUSE_METRICS is off; the Meter counter is added on top when metrics are enabled (R37).
    private static readonly ConcurrentDictionary<DegradedStateKind, long> DegradedCounts = new();

    /// <summary>Whether metrics instrumentation is active for this process.</summary>
    public static bool Enabled { get; }

    static FuseMetrics()
    {
        Enabled = OptIn();
        if (!Enabled)
            return;

        var meter = new Meter("Fuse", FuseBuildInfo.Current);
        ToolDuration = meter.CreateHistogram<double>(
            "fuse.tool.duration",
            unit: "s",
            description: "MCP tool invocation duration in seconds.");
        ReconcileStamped = meter.CreateCounter<long>(
            "fuse.reconcile.stamped",
            description: "Freshness reconciles that degraded to a stale-as-of stamp instead of per-file reconcile.");
        DegradedStates = meter.CreateCounter<long>(
            "fuse.degraded.state",
            description: "Degraded states served (lexical fallback, index rebuilding, integrity failure, index busy, verify abstain).");
        meter.CreateObservableGauge(
            "fuse.index.mode",
            ObserveIndexModes,
            unit: "{mode}",
            description: "Current index mode for a workspace (0 unknown, 1 syntax, 2 partial, 3 semantic).");
    }

    /// <summary>Records an MCP tool invocation duration.</summary>
    /// <param name="toolName">The tool name (for example <c>fuse_find</c>).</param>
    /// <param name="duration">The elapsed wall time.</param>
    public static void RecordToolDuration(string toolName, TimeSpan duration)
    {
        if (!Enabled)
            return;

        ToolDuration!.Record(
            duration.TotalSeconds,
            new KeyValuePair<string, object?>("tool", string.IsNullOrWhiteSpace(toolName) ? "unknown" : toolName));
    }

    /// <summary>Records the current index mode for a workspace root.</summary>
    /// <param name="workspaceRoot">The absolute workspace root.</param>
    /// <param name="mode">The <c>index_mode</c> metadata value.</param>
    public static void RecordIndexMode(string workspaceRoot, string mode)
    {
        if (!Enabled)
            return;

        IndexModesByRoot[workspaceRoot] = string.IsNullOrWhiteSpace(mode) ? "unknown" : mode.Trim();
    }

    /// <summary>Increments the reconcile-stamped counter for a workspace root.</summary>
    /// <param name="workspaceRoot">The absolute workspace root whose reconcile degraded to a stamp.</param>
    public static void RecordReconcileStamped(string workspaceRoot)
    {
        if (!Enabled)
            return;

        ReconcileStamped!.Add(1, new KeyValuePair<string, object?>("workspace", workspaceRoot));
    }

    /// <summary>
    ///     Records that a degraded state was served or returned (R37): loud observability so neither the agent nor
    ///     the operator is left guessing. Always tracked in-process (queryable via <see cref="GetDegradedCount" />)
    ///     and, when <c>FUSE_METRICS</c> is on, emitted as a Meter counter tagged by kind.
    /// </summary>
    /// <param name="kind">The degraded-state kind.</param>
    public static void RecordDegraded(DegradedStateKind kind)
    {
        DegradedCounts.AddOrUpdate(kind, 1, (_, current) => current + 1);
        if (Enabled)
            DegradedStates!.Add(1, new KeyValuePair<string, object?>("kind", kind.ToString()));
    }

    /// <summary>Returns the in-process count of a degraded-state kind (test and doctor seam).</summary>
    /// <param name="kind">The degraded-state kind.</param>
    /// <returns>The number of times the kind was recorded this process.</returns>
    public static long GetDegradedCount(DegradedStateKind kind) =>
        DegradedCounts.TryGetValue(kind, out var count) ? count : 0;

    /// <summary>Resets the in-process degraded-state counts (test seam).</summary>
    public static void ResetDegradedCounts() => DegradedCounts.Clear();

    private static IEnumerable<Measurement<int>> ObserveIndexModes()
    {
        foreach (var pair in IndexModesByRoot)
        {
            yield return new Measurement<int>(
                EncodeIndexMode(pair.Value),
                new KeyValuePair<string, object?>("workspace", pair.Key),
                new KeyValuePair<string, object?>("mode", pair.Value));
        }
    }

    private static int EncodeIndexMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "syntax" => 1,
        "partial" => 2,
        "semantic" => 3,
        _ => 0,
    };

    private static bool OptIn()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_METRICS");
        return value is not null
               && (value.Equals("1", StringComparison.Ordinal)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
///     The degraded states Fuse counts for loud observability (R37), so a fallback or a not-ready read is never
///     silent.
/// </summary>
public enum DegradedStateKind
{
    /// <summary>A read returned a scoped lexical-fallback result instead of a semantic answer (R30).</summary>
    LexicalFallback,

    /// <summary>A read returned <c>index_rebuilding:</c> (derived data is being rebuilt).</summary>
    IndexRebuilding,

    /// <summary>An index-integrity check failed, so the store was not served as ready (R31).</summary>
    IntegrityFailed,

    /// <summary>A read returned <c>index_busy:</c> (writer lock or SQLite contention).</summary>
    IndexBusy,

    /// <summary>A verification abstained (neither oracle nor build grade could run).</summary>
    VerifyAbstained,

    /// <summary>A read deferred to native search because the index was not semantic-ready (R30).</summary>
    Deferred,
}
