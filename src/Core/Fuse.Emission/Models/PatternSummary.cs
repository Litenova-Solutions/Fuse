using Fuse.Plugins.Abstractions.Patterns;

namespace Fuse.Emission.Models;

/// <summary>
///     Summary of detected cross-cutting patterns across fused content.
/// </summary>
/// <param name="Patterns">The detected patterns to summarize.</param>
public sealed record PatternSummary(IReadOnlyList<DetectedPattern> Patterns)
{
    /// <summary>
    ///     Renders the pattern summary as a <c>&lt;!-- fuse:patterns --&gt;</c> XML comment block.
    /// </summary>
    /// <returns>
    ///     A comment block with one <c>name: summary</c> line per pattern, or <see cref="string.Empty" />
    ///     when no patterns were detected.
    /// </returns>
    public string ToComment()
    {
        if (Patterns.Count == 0)
            return string.Empty;

        var lines = Patterns.Select(p => $"{p.PatternName}: {p.Summary}");
        return "<!-- fuse:patterns\n" + string.Join("\n", lines) + "\n-->";
    }
}
