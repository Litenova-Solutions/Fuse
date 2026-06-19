using Fuse.Plugins.Abstractions.Patterns;

namespace Fuse.Analysis.Patterns;

/// <summary>
///     Single-pass driver that offers each fused file to all pattern detectors once.
/// </summary>
public static class PatternDetectionBatch
{
    /// <summary>
    ///     Runs all detectors over the corpus in a single file iteration.
    /// </summary>
    /// <param name="detectors">The detectors to run; each is reset before and completed after the pass.</param>
    /// <param name="snapshots">The fused files offered to every detector in iteration order.</param>
    /// <returns>
    ///     The patterns reported by detectors that produced a non-null result; detectors that find nothing
    ///     are omitted.
    /// </returns>
    /// <remarks>
    ///     Each detector observes every snapshot exactly once via
    ///     <see cref="PatternDetectorBase.AccumulateFile" />, bracketed by
    ///     <see cref="PatternDetectorBase.ResetAccumulation" /> and
    ///     <see cref="PatternDetectorBase.CompleteAccumulation" />. Detectors are heuristic and accumulate
    ///     mutable state, so this method is not safe to run concurrently over the same detector instances.
    /// </remarks>
    public static IReadOnlyList<DetectedPattern> Run(
        IEnumerable<PatternDetectorBase> detectors,
        IReadOnlyList<FusedFileSnapshot> snapshots)
    {
        var active = detectors.ToList();
        foreach (var detector in active)
            detector.ResetAccumulation();

        foreach (var snapshot in snapshots)
        {
            foreach (var detector in active)
                detector.AccumulateFile(snapshot);
        }

        return active
            .Select(d => d.CompleteAccumulation())
            .Where(p => p is not null)
            .Cast<DetectedPattern>()
            .ToArray();
    }
}
