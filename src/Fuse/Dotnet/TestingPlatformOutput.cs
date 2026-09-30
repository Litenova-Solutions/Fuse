using System.Text.RegularExpressions;

namespace Fuse.Dotnet;

/// <summary>
///     Reads what a Microsoft.Testing.Platform run prints: the counts in its run summary, and each failed test's message
///     and the stack frames inside the repository. Such a run writes a TRX file only through its framework's own reporter
///     option, which each framework names differently, while every framework prints the summary and the failures the same
///     way.
/// </summary>
internal static partial class TestingPlatformOutput
{
    /// <summary>
    ///     The counts and failures in <paramref name="output"/>, or null when it has no run summary, as a run that did not
    ///     build has none, or when the summary counts errors.
    /// </summary>
    /// <param name="output">The run's standard output and standard error.</param>
    /// <param name="root">The repository, which frames are printed relative to.</param>
    public static TrxResults? Read(string output, string root)
    {
        var lines = output.Replace("\r", "").Split('\n');
        var summary = Array.FindLastIndex(lines, l => l.StartsWith("Test run summary:", StringComparison.Ordinal));
        if (summary < 0)
            return null;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        // The summary's count lines are indented, with a blank line between some of them.
        for (var i = summary + 1; i < lines.Length && (lines[i].Length == 0 || lines[i].StartsWith("  ", StringComparison.Ordinal)); i++)
        {
            if (CountLine().Match(lines[i]) is { Success: true } count)
                counts[count.Groups["name"].Value] = int.Parse(count.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        // A run with errors, such as an argument the test application rejects, did not run its tests to results.
        if (!counts.TryGetValue("failed", out var failed) || !counts.TryGetValue("succeeded", out var succeeded) || counts.GetValueOrDefault("error") > 0)
            return null;
        counts.TryGetValue("skipped", out var skipped);

        var failures = new List<TestFailure>();
        for (var i = 0; i < summary; i++)
        {
            if (FailedLine().Match(lines[i]) is not { Success: true } header)
                continue;
            var message = new List<string>();
            var stack = new List<string>();
            for (i++; i < summary && lines[i].StartsWith("  ", StringComparison.Ordinal); i++)
            {
                if (lines[i].StartsWith("    at ", StringComparison.Ordinal))
                    stack.Add(lines[i]);
                else if (!lines[i].StartsWith("  from ", StringComparison.Ordinal))
                    message.Add(lines[i][2..]);
            }

            i--;
            failures.Add(new TestFailure(header.Groups["name"].Value, TrxReader.Trim(string.Join('\n', message), 12), TrxReader.Frames(string.Join('\n', stack), root)));
        }

        return new TrxResults(succeeded, failed, skipped, failures);
    }

    [GeneratedRegex(@"^  (?<name>[a-z]+): (?<value>\d+)")]
    private static partial Regex CountLine();

    [GeneratedRegex(@"^failed (?<name>.+) \((?:\d+(?:ms|s|m|h) ?)+\)$")]
    private static partial Regex FailedLine();
}
