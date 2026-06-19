namespace Fuse.Analysis.Patterns;

/// <summary>
///     A detected cross-cutting code pattern.
/// </summary>
public sealed record DetectedPattern(
    string PatternName,
    string Summary,
    int OccurrenceCount,
    IReadOnlyList<string> Examples);
