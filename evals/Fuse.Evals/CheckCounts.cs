using System.Text.RegularExpressions;
using Fuse.Telemetry;

namespace Fuse.Evals;

/// <summary>
///     What one <c>fuse check</c> did and did not do: how many files it checked, how many documents it bound, whether it
///     checked whole projects, whether it answered from the answer it kept, and for how many files it reused the errors of
///     an earlier check. Read from the check's summary line, its <c>check:</c> engine-log line and its phase line, so a
///     logging regression reads as absent rather than as zero work.
/// </summary>
internal sealed partial record CheckCounts(
    int? FilesChecked,
    int? DocumentsBound,
    bool WholeProjectsBound,
    bool KeptAnswerHit,
    int? FilesWithReusedErrors)
{
    /// <summary>Whether the engine wrote a <c>check:</c> line for this case, without which the counts are unmeasured.</summary>
    public bool HasCheckLine { get; init; }

    /// <summary>
    ///     Reads the counts for one check from its console <paramref name="output"/>, the engine-log <paramref name="lines"/>
    ///     written since it started, and its <paramref name="phases"/>. A clean summary carries the files checked; an
    ///     answer with errors does not, so those come from the <c>check:</c> line's files bound instead, which is the same
    ///     number, and stay null for a kept answer to an error check, which logs neither.
    /// </summary>
    public static CheckCounts Parse(string output, IReadOnlyList<string> lines, RequestPhases? phases)
    {
        var check = lines.LastOrDefault(l => l.StartsWith("check:", StringComparison.Ordinal));
        var kept = check is not null && check.StartsWith("check: kept answer", StringComparison.Ordinal);
        int? files = null;
        if (CheckedSummary().Match(output) is { Success: true } summary)
            files = int.Parse(summary.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        else if (check is not null && BoundLine().Match(check) is { Success: true } bound)
            files = int.Parse(bound.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        int? documents = phases is null ? null : (int)Math.Round(phases.Phases.GetValueOrDefault(Phase.BindFrameworks));
        var whole = output.Contains("checked whole projects", StringComparison.Ordinal)
            || (check is not null && check.Contains(", whole projects", StringComparison.Ordinal));
        int? reused = check is null ? null
            : ReusedLine().Match(check) is { Success: true } reusedMatch
            ? int.Parse(reusedMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return new CheckCounts(files, documents, whole, kept, reused) { HasCheckLine = check is not null };
    }

    [GeneratedRegex(@"\((\d+) file\(s\) checked\)", RegexOptions.CultureInvariant)]
    private static partial Regex CheckedSummary();

    [GeneratedRegex(@"(\d+) file\(s\) bound", RegexOptions.CultureInvariant)]
    private static partial Regex BoundLine();

    [GeneratedRegex(@"\((\d+) with errors reused from an earlier check\)", RegexOptions.CultureInvariant)]
    private static partial Regex ReusedLine();
}
