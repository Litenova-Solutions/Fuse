using System.Text.RegularExpressions;
using Fuse.Reduction.Models;

namespace Fuse.Analysis.Patterns;

/// <summary>
///     Detects DI registration patterns (AddSingleton, AddScoped, AddTransient).
/// </summary>
public sealed class DiRegistrationPatternDetector : IPatternDetector
{
    private static readonly Regex RegistrationRegex = new(
        @"Add(Singleton|Scoped|Transient)\s*[<\(]",
        RegexOptions.Compiled);

    /// <inheritdoc />
    public string PatternName => "DI Registration";

    /// <inheritdoc />
    public DetectedPattern? Detect(IReadOnlyList<FusedContent> fusedFiles)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var examples = new List<string>();

        foreach (var file in fusedFiles)
        {
            foreach (Match match in RegistrationRegex.Matches(file.Content))
            {
                var kind = "Add" + match.Groups[1].Value;
                counts[kind] = counts.GetValueOrDefault(kind) + 1;
                if (examples.Count < 3)
                    examples.Add(file.NormalizedPath);
            }
        }

        if (counts.Count == 0)
            return null;

        var total = counts.Values.Sum();
        var parts = counts.Select(kvp => $"{kvp.Key} ({kvp.Value} occurrences)");
        return new DetectedPattern(PatternName, string.Join(" | ", parts), total, examples);
    }
}
