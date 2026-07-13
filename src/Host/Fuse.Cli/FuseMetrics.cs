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
