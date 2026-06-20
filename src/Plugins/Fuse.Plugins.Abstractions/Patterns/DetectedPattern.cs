namespace Fuse.Plugins.Abstractions.Patterns;

/// <summary>
///     A detected cross-cutting code pattern, produced by an <see cref="IPatternDetector" />.
/// </summary>
/// <param name="PatternName">Display name of the detected pattern.</param>
/// <param name="Summary">Human-readable description of what was detected.</param>
/// <param name="OccurrenceCount">Number of occurrences found across the fused files.</param>
/// <param name="Examples">Representative example snippets or locations illustrating the pattern.</param>
public sealed record DetectedPattern(
    string PatternName,
    string Summary,
    int OccurrenceCount,
    IReadOnlyList<string> Examples);
