using System.Text.RegularExpressions;

namespace Fuse.Evals;

/// <summary>
///     Reads the full results <c>fuse check</c> and <c>fuse test</c> write beside a capped answer, so a suite can check the
///     answer proves its own completeness: the file holds every error or failure the summary counts. A capped summary names
///     its file (<c>; all N in path</c>), which is always the run that just ended; any other read is checked for freshness,
///     because a run that wrote nothing leaves the previous run's file behind.
/// </summary>
internal static partial class ResultsFileCounts
{
    /// <summary>The capped answer's total and results-file path from its summary, or null when the answer is uncapped.</summary>
    public static (int Total, string Path)? CappedResults(string output)
    {
        var match = Capped().Match(output);
        return match.Success
            ? (int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), match.Groups[2].Value.Trim())
            : null;
    }

    /// <summary>The file's text, or null when it is missing or cannot be read.</summary>
    public static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the file was written since <paramref name="startUtc"/>.</summary>
    public static bool IsFresh(string path, DateTime startUtc)
    {
        try
        {
            return File.Exists(path) && File.GetLastWriteTimeUtc(path) >= startUtc;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>How many canonical error lines the check results file holds; its cause lines are indented and never match.</summary>
    public static int CheckErrorsIn(string text) => EvalRepo.ParseFuseErrors(text).Count;

    /// <summary>How many errors the check results file says the engine did not send, or 0 when it says nothing of the kind.</summary>
    public static int EngineUnsentIn(string text)
    {
        var match = EngineUnsent().Match(text);
        return match.Success ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>How many <c>FAILED</c> lines the test results file holds, which details every failure without a cap.</summary>
    public static int TestFailuresIn(string text) =>
        text.Split('\n').Count(l => l.StartsWith("FAILED ", StringComparison.Ordinal));

    /// <summary>How many per-project groups the test results file holds, one <c>## project</c> header per project with failures.</summary>
    public static int ProjectGroupsIn(string text) =>
        text.Split('\n').Count(l => l.StartsWith("## ", StringComparison.Ordinal));

    [GeneratedRegex(@"; all (\d+) in (.+?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Capped();

    [GeneratedRegex(@"\.\.\. and (\d+) more error\(s\) the engine did not send", RegexOptions.CultureInvariant)]
    private static partial Regex EngineUnsent();
}
