using System.Text.RegularExpressions;
using Fuse.Languages.Abstractions.Patterns;

namespace Fuse.Languages.CSharp.Patterns;

/// <summary>
///     Detects custom exception types and catch blocks.
/// </summary>
public sealed partial class ExceptionHandlingPatternDetector : PatternDetectorBase
{
    private readonly HashSet<string> _exceptionTypes = new(StringComparer.Ordinal);
    private int _catchCount;
    private readonly List<string> _examples = [];

    /// <inheritdoc />
    public override string PatternName => "Exception Handling";

    /// <inheritdoc />
    protected override void ResetInternal()
    {
        _exceptionTypes.Clear();
        _catchCount = 0;
        _examples.Clear();
    }

    /// <inheritdoc />
    protected override void AnalyzeFile(FusedFileSnapshot file)
    {
        foreach (Match match in ExceptionTypeRegex().Matches(file.Content))
        {
            _exceptionTypes.Add(match.Groups[1].Value);
            if (_examples.Count < 3)
                _examples.Add(file.NormalizedPath);
        }

        _catchCount += CatchRegex().Matches(file.Content).Count;
    }

    /// <inheritdoc />
    protected override DetectedPattern? BuildResult()
    {
        if (_exceptionTypes.Count == 0 && _catchCount == 0)
            return null;

        var typeList = string.Join(", ", _exceptionTypes.OrderBy(t => t));
        var summary = _exceptionTypes.Count > 0
            ? $"Custom exception types detected: {typeList}"
            : $"catch blocks detected ({_catchCount} occurrences)";

        return new DetectedPattern(PatternName, summary, _exceptionTypes.Count + _catchCount, _examples);
    }

    [GeneratedRegex(@"\b(\w+Exception)\s*:\s*Exception\b", RegexOptions.Compiled)]
    private static partial Regex ExceptionTypeRegex();

    [GeneratedRegex(@"\bcatch\s*\(", RegexOptions.Compiled)]
    private static partial Regex CatchRegex();
}
