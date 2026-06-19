using System.Text.RegularExpressions;
using Fuse.Reduction.Models;

namespace Fuse.Analysis.Patterns;

/// <summary>
///     Detects ILogger injection patterns.
/// </summary>
public sealed class LoggingPatternDetector : IPatternDetector
{
    private static readonly Regex LoggerRegex = new(@"ILogger\s*<\s*\w+\s*>", RegexOptions.Compiled);

    /// <inheritdoc />
    public string PatternName => "Logging";

    /// <inheritdoc />
    public DetectedPattern? Detect(IReadOnlyList<FusedContent> fusedFiles)
    {
        var count = 0;
        var fileCount = 0;
        var examples = new List<string>();

        foreach (var file in fusedFiles)
        {
            var matches = LoggerRegex.Matches(file.Content).Count;
            if (matches > 0)
            {
                count += matches;
                fileCount++;
                if (examples.Count < 3)
                    examples.Add(file.NormalizedPath);
            }
        }

        if (count == 0)
            return null;

        return new DetectedPattern(
            PatternName,
            $"ILogger<T> injection detected in {fileCount} files",
            count,
            examples);
    }
}
