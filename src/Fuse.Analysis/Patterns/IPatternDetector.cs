using Fuse.Reduction.Models;

namespace Fuse.Analysis.Patterns;

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
    DetectedPattern? Detect(IReadOnlyList<FusedContent> fusedFiles);
}
