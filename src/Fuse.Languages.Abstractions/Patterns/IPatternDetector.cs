namespace Fuse.Languages.Abstractions.Patterns;

/// <summary>
///     Detects a cross-cutting code pattern across fused content.
/// </summary>
public interface IPatternDetector
{
    /// <summary>
    ///     Gets the display name of the pattern.
    /// </summary>
    string PatternName { get; }

    /// <summary>
    ///     Detects the pattern in the supplied fused files, or returns null when not found.
    /// </summary>
    /// <param name="fusedFiles">Fused file snapshots to analyze.</param>
    /// <returns>A detected pattern summary, or <see langword="null" /> when the pattern is absent.</returns>
    DetectedPattern? Detect(IReadOnlyList<FusedFileSnapshot> fusedFiles);
}
