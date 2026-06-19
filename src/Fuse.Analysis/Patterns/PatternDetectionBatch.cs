using Fuse.Languages.Abstractions.Patterns;

namespace Fuse.Analysis.Patterns;

/// <summary>
///     Single-pass driver that offers each fused file to all pattern detectors once.
/// </summary>
public static class PatternDetectionBatch
{
    /// <summary>
    ///     Runs all detectors over the corpus in a single file iteration.
    /// </summary>
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
