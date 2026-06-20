using System.Text.RegularExpressions;
using Fuse.Plugins.Abstractions.Patterns;

namespace Fuse.Plugins.Languages.CSharp.Patterns;

/// <summary>
///     Detects dependency injection registration patterns (<c>AddSingleton</c>, <c>AddScoped</c>,
///     and <c>AddTransient</c>) and tallies each kind.
/// </summary>
/// <remarks>
///     Matching keys on the method name followed by <c>&lt;</c> or <c>(</c>, so identically named
///     extension methods from unrelated APIs can be counted as false positives.
/// </remarks>
public sealed partial class DiRegistrationPatternDetector : PatternDetectorBase
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly List<string> _examples = [];

    /// <inheritdoc />
    public override string PatternName => "DI Registration";

    /// <inheritdoc />
    protected override void ResetInternal()
    {
        _counts.Clear();
        _examples.Clear();
    }

    /// <inheritdoc />
    protected override void AnalyzeFile(FusedFileSnapshot file)
    {
        foreach (Match match in RegistrationRegex().Matches(file.Content))
        {
            var kind = "Add" + match.Groups[1].Value;
            _counts[kind] = _counts.GetValueOrDefault(kind) + 1;
            if (_examples.Count < 3)
                _examples.Add(file.NormalizedPath);
        }
    }

    /// <inheritdoc />
    protected override DetectedPattern? BuildResult()
    {
        if (_counts.Count == 0)
            return null;

        var total = _counts.Values.Sum();
        var parts = _counts.Select(kvp => $"{kvp.Key} ({kvp.Value} occurrences)");
        return new DetectedPattern(PatternName, string.Join(" | ", parts), total, _examples);
    }

    [GeneratedRegex(@"Add(Singleton|Scoped|Transient)\s*[<\(]", RegexOptions.Compiled)]
    private static partial Regex RegistrationRegex();
}
