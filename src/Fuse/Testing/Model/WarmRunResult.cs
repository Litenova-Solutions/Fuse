namespace Fuse.Testing.Model;

/// <summary>Result of running a test slice through <c>WarmTestPool</c>.</summary>
/// <param name="ExecutedInWarmWorker">True when tests ran inside a warm worker (or hit the slice cache); false when standard shadow fallback is required.</param>
/// <param name="FromCache">True when satisfied directly from <c>SliceTestCache</c>.</param>
/// <param name="EncApplied">True when Tier 2 <c>MetadataUpdater.ApplyUpdate</c> patched the running worker in-place.</param>
/// <param name="StoppedEarly">True when execution stopped after reaching <c>MaxFailures</c>.</param>
/// <param name="Passed">Passing test count.</param>
/// <param name="Failed">Failing test count.</param>
/// <param name="Skipped">Skipped test count.</param>
/// <param name="DurationMs">Execution time in milliseconds.</param>
/// <param name="FallbackReason">Non-null reason when the warm worker declined, timed out or crashed and caller should fall back to standard shadow execution.</param>
/// <param name="Failures">Details of failed tests.</param>
internal sealed record WarmRunResult(
    bool ExecutedInWarmWorker,
    bool FromCache,
    bool EncApplied,
    bool StoppedEarly,
    int Passed,
    int Failed,
    int Skipped,
    double DurationMs,
    string? FallbackReason,
    IReadOnlyList<SliceTestFailure> Failures)
{
    /// <summary>Returns a result indicating standard shadow execution should run instead.</summary>
    public static WarmRunResult Fallback(string reason, double durationMs = 0) =>
        new(false, false, false, false, 0, 0, 0, durationMs, reason, []);
}
