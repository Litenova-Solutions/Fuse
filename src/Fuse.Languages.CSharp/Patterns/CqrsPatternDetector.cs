using System.Text.RegularExpressions;
using Fuse.Languages.Abstractions.Patterns;

namespace Fuse.Languages.CSharp.Patterns;

/// <summary>
///     Detects CQRS / MediatR patterns.
/// </summary>
public sealed partial class CqrsPatternDetector : PatternDetectorBase
{
    private readonly HashSet<string> _found = new(StringComparer.Ordinal);
    private readonly List<string> _examples = [];

    /// <inheritdoc />
    public override string PatternName => "CQRS";

    /// <inheritdoc />
    protected override void ResetInternal()
    {
        _found.Clear();
        _examples.Clear();
    }

    /// <inheritdoc />
    protected override void AnalyzeFile(FusedFileSnapshot file)
    {
        foreach (Match match in CqrsRegex().Matches(file.Content))
        {
            _found.Add(match.Groups[1].Value);
            if (_examples.Count < 3)
                _examples.Add(file.NormalizedPath);
        }
    }

    /// <inheritdoc />
    protected override DetectedPattern? BuildResult()
    {
        if (_found.Count == 0)
            return null;

        var primary = _found.Contains("IRequest") ? "IRequest detected (MediatR pattern)"
            : string.Join(", ", _found.OrderBy(f => f)) + " detected";

        return new DetectedPattern(PatternName, primary, _found.Count, _examples);
    }

    [GeneratedRegex(@"\b(IRequest|ICommand|IQuery|IMediator)\b", RegexOptions.Compiled)]
    private static partial Regex CqrsRegex();
}
