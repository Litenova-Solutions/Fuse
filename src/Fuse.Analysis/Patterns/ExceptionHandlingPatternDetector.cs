using System.Text.RegularExpressions;
using Fuse.Reduction.Models;

namespace Fuse.Analysis.Patterns;

/// <summary>
///     Detects custom exception types and catch blocks.
/// </summary>
public sealed class ExceptionHandlingPatternDetector : IPatternDetector
{
    private static readonly Regex ExceptionTypeRegex = new(
        @"\b(\w+Exception)\s*:\s*Exception\b",
        RegexOptions.Compiled);

    /// <inheritdoc />
    public string PatternName => "Exception Handling";

    /// <inheritdoc />
    public DetectedPattern? Detect(IReadOnlyList<FusedContent> fusedFiles)
    {
        var exceptionTypes = new HashSet<string>(StringComparer.Ordinal);
        var catchCount = 0;
        var examples = new List<string>();

        foreach (var file in fusedFiles)
        {
            foreach (Match match in ExceptionTypeRegex.Matches(file.Content))
            {
                exceptionTypes.Add(match.Groups[1].Value);
                if (examples.Count < 3)
                    examples.Add(file.NormalizedPath);
            }

            catchCount += Regex.Matches(file.Content, @"\bcatch\s*\(").Count;
        }

        if (exceptionTypes.Count == 0 && catchCount == 0)
            return null;

        var typeList = string.Join(", ", exceptionTypes.OrderBy(t => t));
        var summary = exceptionTypes.Count > 0
            ? $"Custom exception types detected: {typeList}"
            : $"catch blocks detected ({catchCount} occurrences)";

        return new DetectedPattern(PatternName, summary, exceptionTypes.Count + catchCount, examples);
    }
}
