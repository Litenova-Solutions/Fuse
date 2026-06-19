using Fuse.Languages.Abstractions.Patterns;

namespace Fuse.Analysis.Patterns;

/// <summary>
///     Summary of detected cross-cutting patterns across fused content.
/// </summary>
public sealed record PatternSummary(IReadOnlyList<DetectedPattern> Patterns)
{
    /// <summary>
    ///     Renders the pattern summary as an XML comment block.
    /// </summary>
    public string ToComment()
    {
        if (Patterns.Count == 0)
            return string.Empty;

        var lines = Patterns.Select(p => $"{p.PatternName}: {p.Summary}");
        return "<!-- fuse:patterns\n" + string.Join("\n", lines) + "\n-->";
    }
}
