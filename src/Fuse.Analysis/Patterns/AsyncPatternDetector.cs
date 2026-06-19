using System.Text.RegularExpressions;
using Fuse.Reduction.Models;

namespace Fuse.Analysis.Patterns;

/// <summary>
///     Detects async Task/ValueTask patterns and ConfigureAwait usage.
/// </summary>
public sealed class AsyncPatternDetector : IPatternDetector
{
    /// <inheritdoc />
    public string PatternName => "Async Pattern";

    /// <inheritdoc />
    public DetectedPattern? Detect(IReadOnlyList<FusedContent> fusedFiles)
    {
        var taskCount = 0;
        var valueTaskCount = 0;
        var configureAwaitCount = 0;
        var examples = new List<string>();

        foreach (var file in fusedFiles)
        {
            taskCount += Regex.Matches(file.Content, @"\basync\s+Task\b").Count;
            valueTaskCount += Regex.Matches(file.Content, @"\basync\s+ValueTask\b").Count;
            configureAwaitCount += Regex.Matches(file.Content, @"ConfigureAwait\s*\(").Count;

            if (examples.Count < 3 && (taskCount > 0 || valueTaskCount > 0))
                examples.Add(file.NormalizedPath);
        }

        if (taskCount == 0 && valueTaskCount == 0)
            return null;

        var parts = new List<string>();
        if (taskCount > 0)
            parts.Add($"async Task ({taskCount} occurrences)");
        if (valueTaskCount > 0)
            parts.Add($"async ValueTask ({valueTaskCount} occurrences)");

        parts.Add(configureAwaitCount > 0
            ? "ConfigureAwait used"
            : "ConfigureAwait not used");

        return new DetectedPattern(
            PatternName,
            string.Join(" | ", parts),
            taskCount + valueTaskCount,
            examples);
    }
}
