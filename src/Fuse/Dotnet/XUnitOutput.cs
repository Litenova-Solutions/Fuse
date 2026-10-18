using System.Globalization;
using System.Text.RegularExpressions;

namespace Fuse.Dotnet;

/// <summary>
///     Reads what the xUnit v3 test application prints when it runs on its own, outside <c>dotnet test</c> on
///     Microsoft.Testing.Platform: the counts in its execution summary, and each failed test's message and the stack
///     frames inside the repository. Under <c>dotnet test</c> the same application prints the platform's summary
///     instead, which <see cref="TestingPlatformOutput"/> reads.
/// </summary>
internal static partial class XUnitOutput
{
    /// <summary>
    ///     The counts and failures in <paramref name="output"/>, or null when it has no execution summary, as a run
    ///     that did not start has none, or when the summary counts errors.
    /// </summary>
    /// <param name="output">The run's standard output and standard error.</param>
    /// <param name="root">The repository, which frames are printed relative to.</param>
    public static TrxResults? Read(string output, string root)
    {
        var lines = AnsiSequences.Strip(output).Replace("\r", "").Split('\n');
        var summary = Array.FindLastIndex(lines, l => l.StartsWith("=== TEST EXECUTION SUMMARY ===", StringComparison.Ordinal));
        if (summary < 0)
            return null;

        var (total, failed, skipped, ran) = (0, 0, 0, false);
        for (var i = summary + 1; i < lines.Length; i++)
        {
            if (SummaryLine().Match(lines[i]) is not { Success: true } counts)
                continue;
            // A run with errors, such as an argument the application rejects, did not run its tests to results.
            if (Count(counts, "errors") > 0)
                return null;
            total += Count(counts, "total");
            failed += Count(counts, "failed");
            skipped += Count(counts, "skipped") + Count(counts, "notrun");
            ran = true;
        }

        if (!ran)
            return null;

        var failures = new List<TestFailure>();
        for (var i = 0; i < summary; i++)
        {
            if (FailedLine().Match(lines[i]) is not { Success: true } header)
                continue;
            var message = new List<string>();
            var stack = new List<string>();
            var inStack = false;
            for (i++; i < summary && lines[i].StartsWith("      ", StringComparison.Ordinal); i++)
            {
                if (lines[i].Trim() == "Stack Trace:")
                    inStack = true;
                else if (inStack)
                    stack.Add(Frame(lines[i].Trim()));
                else
                    message.Add(lines[i][6..]);
            }

            i--;
            failures.Add(new TestFailure(header.Groups["name"].Value, TrxReader.Trim(string.Join('\n', message), 12), TrxReader.Frames(string.Join('\n', stack), root)));
        }

        return new TrxResults(total - failed - skipped, failed, skipped, failures);
    }

    private static int Count(Match counts, string name) =>
        counts.Groups[name] is { Success: true } group ? int.Parse(group.Value, CultureInfo.InvariantCulture) : 0;

    /// <summary>One frame as a VSTest stack writes it (<c>at member in file:line n</c>), which <see cref="TrxReader.Frames"/> reads.</summary>
    private static string Frame(string line) =>
        FrameLine().Match(line) is { Success: true } frame
            ? $"at {frame.Groups["member"].Value} in {frame.Groups["path"].Value}:line {frame.Groups["line"].Value}"
            : line;

    [GeneratedRegex(@"^\s+\S.*?  Total: (?<total>\d+)(?:, Errors: (?<errors>\d+), Failed: (?<failed>\d+), Skipped: (?<skipped>\d+), Not Run: (?<notrun>\d+))?")]
    private static partial Regex SummaryLine();

    [GeneratedRegex(@"^    (?<name>.+) \[FAIL\]$")]
    private static partial Regex FailedLine();

    [GeneratedRegex(@"^(?<path>.+)\((?<line>\d+),\d+\): at (?<member>.+)$")]
    private static partial Regex FrameLine();
}
