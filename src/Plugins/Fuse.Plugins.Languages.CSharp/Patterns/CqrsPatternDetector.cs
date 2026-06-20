using System.Text.RegularExpressions;
using Fuse.Plugins.Abstractions.Patterns;

namespace Fuse.Plugins.Languages.CSharp.Patterns;

/// <summary>
///     Detects CQRS and MediatR patterns by matching <c>IRequest</c>, <c>ICommand</c>, <c>IQuery</c>,
///     and <c>IMediator</c> references.
/// </summary>
/// <remarks>
///     Detection is name-based and may produce false positives: same-named interfaces from unrelated
///     libraries (or types mentioned in comments) are counted, and the marker does not confirm a real
///     MediatR dependency.
/// </remarks>
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
