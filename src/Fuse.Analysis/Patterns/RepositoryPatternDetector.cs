using System.Text.RegularExpressions;
using Fuse.Reduction.Models;

namespace Fuse.Analysis.Patterns;

/// <summary>
///     Detects repository pattern type references.
/// </summary>
public sealed class RepositoryPatternDetector : IPatternDetector
{
    private static readonly Regex RepositoryRegex = new(
        @"\b(IRepository\s*<|IRepository\b|Repository\s*<)",
        RegexOptions.Compiled);

    /// <inheritdoc />
    public string PatternName => "Repository";

    /// <inheritdoc />
    public DetectedPattern? Detect(IReadOnlyList<FusedContent> fusedFiles)
    {
        var count = 0;
        var fileCount = 0;
        var examples = new List<string>();

        foreach (var file in fusedFiles)
        {
            var matches = RepositoryRegex.Matches(file.Content).Count;
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
            $"IRepository<T> detected in {fileCount} files",
            count,
            examples);
    }
}
