using System.Text.RegularExpressions;
using Fuse.Languages.Abstractions.Patterns;

namespace Fuse.Languages.CSharp.Patterns;

/// <summary>
///     Detects async Task/ValueTask patterns and ConfigureAwait usage.
/// </summary>
public sealed partial class AsyncPatternDetector : PatternDetectorBase
{
    private int _taskCount;
    private int _valueTaskCount;
    private int _configureAwaitCount;
    private readonly List<string> _examples = [];

    /// <inheritdoc />
    public override string PatternName => "Async Pattern";

    /// <inheritdoc />
    protected override void ResetInternal()
    {
        _taskCount = 0;
        _valueTaskCount = 0;
        _configureAwaitCount = 0;
        _examples.Clear();
    }

    /// <inheritdoc />
    protected override void AnalyzeFile(FusedFileSnapshot file)
    {
        _taskCount += AsyncTaskRegex().Matches(file.Content).Count;
        _valueTaskCount += AsyncValueTaskRegex().Matches(file.Content).Count;
        _configureAwaitCount += ConfigureAwaitRegex().Matches(file.Content).Count;

        if (_examples.Count < 3 && (_taskCount > 0 || _valueTaskCount > 0))
            _examples.Add(file.NormalizedPath);
    }

    /// <inheritdoc />
    protected override DetectedPattern? BuildResult()
    {
        if (_taskCount == 0 && _valueTaskCount == 0)
            return null;

        var parts = new List<string>();
        if (_taskCount > 0)
            parts.Add($"async Task ({_taskCount} occurrences)");
        if (_valueTaskCount > 0)
            parts.Add($"async ValueTask ({_valueTaskCount} occurrences)");

        parts.Add(_configureAwaitCount > 0
            ? "ConfigureAwait used"
            : "ConfigureAwait not used");

        return new DetectedPattern(
            PatternName,
            string.Join(" | ", parts),
            _taskCount + _valueTaskCount,
            _examples);
    }

    [GeneratedRegex(@"\basync\s+Task\b", RegexOptions.Compiled)]
    private static partial Regex AsyncTaskRegex();

    [GeneratedRegex(@"\basync\s+ValueTask\b", RegexOptions.Compiled)]
    private static partial Regex AsyncValueTaskRegex();

    [GeneratedRegex(@"ConfigureAwait\s*\(", RegexOptions.Compiled)]
    private static partial Regex ConfigureAwaitRegex();
}
