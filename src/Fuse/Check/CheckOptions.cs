namespace Fuse.Check;

/// <summary>Optional controls for <see cref="Checker.CheckAsync(Model.CheckScope, string?, AnalyzerRun, Telemetry.PhaseTimes, CheckOptions, CancellationToken, int)"/>.</summary>
/// <param name="InteractiveFast">
///     True to bind only edited member body spans when a target has body-only edits (skipping whole-file binding and
///     compilation-wide analyzers). Automatically falls back to full-file binding when surface, usings, or code
///     outside member bodies changed.
/// </param>
/// <param name="IncludeRepairBrief">
///     True to attach a deterministic <see cref="Model.RepairPacket"/> to each introduced error that supports one.
/// </param>
internal sealed record CheckOptions(bool InteractiveFast = false, bool IncludeRepairBrief = false)
{
    /// <summary>Standard full check options with no repair packets.</summary>
    public static readonly CheckOptions Default = new();
}
