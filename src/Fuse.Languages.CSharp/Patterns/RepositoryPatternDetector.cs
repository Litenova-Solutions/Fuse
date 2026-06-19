using System.Text.RegularExpressions;
using Fuse.Languages.Abstractions.Patterns;

namespace Fuse.Languages.CSharp.Patterns;

/// <summary>
///     Detects repository pattern type references.
/// </summary>
public sealed partial class RepositoryPatternDetector : PatternDetectorBase
{
    private int _count;
    private int _fileCount;
    private readonly List<string> _examples = [];

    /// <inheritdoc />
    public override string PatternName => "Repository";

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
        var matches = RepositoryRegex().Matches(file.Content).Count;
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
            $"IRepository<T> detected in {_fileCount} files",
            _count,
            _examples);
    }

    [GeneratedRegex(@"\b(IRepository\s*<|IRepository\b|Repository\s*<)", RegexOptions.Compiled)]
    private static partial Regex RepositoryRegex();
}
