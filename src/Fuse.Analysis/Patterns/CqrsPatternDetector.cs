using System.Text.RegularExpressions;
using Fuse.Reduction.Models;

namespace Fuse.Analysis.Patterns;

/// <summary>
///     Detects CQRS / MediatR patterns.
/// </summary>
public sealed class CqrsPatternDetector : IPatternDetector
{
    private static readonly Regex CqrsRegex = new(
        @"\b(IRequest|ICommand|IQuery|IMediator)\b",
        RegexOptions.Compiled);

    /// <inheritdoc />
    public string PatternName => "CQRS";

    /// <inheritdoc />
    public DetectedPattern? Detect(IReadOnlyList<FusedContent> fusedFiles)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var examples = new List<string>();

        foreach (var file in fusedFiles)
        {
            foreach (Match match in CqrsRegex.Matches(file.Content))
            {
                found.Add(match.Groups[1].Value);
                if (examples.Count < 3)
                    examples.Add(file.NormalizedPath);
            }
        }

        if (found.Count == 0)
            return null;

        var primary = found.Contains("IRequest") ? "IRequest detected (MediatR pattern)"
            : string.Join(", ", found.OrderBy(f => f)) + " detected";

        return new DetectedPattern(PatternName, primary, found.Count, examples);
    }
}
