using System.Text.RegularExpressions;
using Fuse.Plugins.Abstractions.Patterns;

namespace Fuse.Plugins.Languages.CSharp.Patterns;

/// <summary>
///     Detects async <see cref="System.Threading.Tasks.Task" />/<see cref="System.Threading.Tasks.ValueTask" />
///     patterns and <c>ConfigureAwait</c> usage across fused C# content.
/// </summary>
/// <remarks>
///     Matching is regex-based and counts textual occurrences, so figures are approximate: <c>async Task</c>
///     and <c>async ValueTask</c> mentioned inside comments or string literals are counted, and the
///     <c>ConfigureAwait used</c> verdict reflects only whether the token appears anywhere, not per call site.
/// </remarks>
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
