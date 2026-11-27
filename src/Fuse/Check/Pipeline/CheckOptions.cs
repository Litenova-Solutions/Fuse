using Fuse.Check.Analyzers;
namespace Fuse.Check.Pipeline;

/// <summary>Optional controls for <see cref="Checker.CheckAsync(Model.CheckScope, string?, AnalyzerRun, Telemetry.PhaseTimes, CheckOptions, CancellationToken, int)"/>.</summary>
/// <param name="InteractiveFast">
///     True to bind only edited member body spans when a target has body-only edits, running the per-file analyzers
///     on those spans. Compilation-wide analyzers cannot run on spans: when the check runs them the file falls back
///     to full-file binding, and when the check leaves them out they stay out. Also falls back when surface, usings,
///     or code outside member bodies changed.
/// </param>
/// <param name="IncludeRepairBrief">
///     True to attach a deterministic <see cref="Model.RepairPacket"/> to each introduced error that supports one.
/// </param>
internal sealed record CheckOptions(bool InteractiveFast = false, bool IncludeRepairBrief = false)
{
    /// <summary>Standard full check options with no repair packets.</summary>
    public static readonly CheckOptions Default = new();
}
