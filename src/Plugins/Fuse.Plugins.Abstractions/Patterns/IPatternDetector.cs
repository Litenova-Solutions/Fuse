namespace Fuse.Plugins.Abstractions.Patterns;

/// <summary>
///     Detects a single cross-cutting code pattern across the full set of fused files.
/// </summary>
/// <remarks>
///     Detection is heuristic and scans reduced content rather than a parsed model, so results favor recall
///     over precision and may include false positives. See <see cref="PatternDetectorBase" /> for the
///     batch-accumulation base class implementations typically derive from.
/// </remarks>
public interface IPatternDetector
{
    /// <summary>
    ///     The display name of the pattern, used to label the detected summary.
    /// </summary>
    string PatternName { get; }

    /// <summary>
    ///     Detects the pattern across the supplied fused files.
    /// </summary>
    /// <param name="fusedFiles">The fused file snapshots to analyze.</param>
    /// <returns>
    ///     A <see cref="DetectedPattern" /> summarizing the occurrences, or <see langword="null" /> when the
    ///     pattern is absent.
    /// </returns>
    DetectedPattern? Detect(IReadOnlyList<FusedFileSnapshot> fusedFiles);
}
