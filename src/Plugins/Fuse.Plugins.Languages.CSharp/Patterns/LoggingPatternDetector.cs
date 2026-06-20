using System.Text.RegularExpressions;
using Fuse.Plugins.Abstractions.Patterns;

namespace Fuse.Plugins.Languages.CSharp.Patterns;

/// <summary>
///     Detects <c>ILogger&lt;T&gt;</c> injection patterns across fused C# content.
/// </summary>
/// <remarks>
///     Matching is purely textual on the generic <c>ILogger&lt;T&gt;</c> shape, so non-generic <c>ILogger</c>
///     usages are not counted and any occurrence inside comments or strings is counted as a false positive.
/// </remarks>
public sealed partial class LoggingPatternDetector : PatternDetectorBase
{
    private int _count;
    private int _fileCount;
    private readonly List<string> _examples = [];

    /// <inheritdoc />
    public override string PatternName => "Logging";

    /// <inheritdoc />
    protected override void ResetInternal()
    {
        _count = 0;
        _fileCount = 0;
        _examples.Clear();
    }

    /// <inheritdoc />
    protected override void AnalyzeFile(FusedFileSnapshot file)
    {
        var matches = LoggerRegex().Matches(file.Content).Count;
        if (matches > 0)
        {
            _count += matches;
            _fileCount++;
            if (_examples.Count < 3)
                _examples.Add(file.NormalizedPath);
        }
    }

    /// <inheritdoc />
    protected override DetectedPattern? BuildResult()
    {
        if (_count == 0)
            return null;

        return new DetectedPattern(
            PatternName,
            $"ILogger<T> injection detected in {_fileCount} files",
            _count,
            _examples);
    }

    [GeneratedRegex(@"ILogger\s*<\s*\w+\s*>", RegexOptions.Compiled)]
    private static partial Regex LoggerRegex();
}
