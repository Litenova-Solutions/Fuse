using System.Text.RegularExpressions;
using Fuse.Languages.Abstractions.Patterns;

namespace Fuse.Languages.CSharp.Patterns;

/// <summary>
///     Detects ILogger injection patterns.
/// </summary>
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
