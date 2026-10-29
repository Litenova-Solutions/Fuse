using System.Text.RegularExpressions;

namespace Fuse.Evals;

/// <summary>
///     What one <c>fuse test</c> planned and ran: how many projects it planned, how many ran without MSBuild and how many
///     built with it, how many ran whole and why, which filters collapsed, and how many test processes the plan implies.
///     Read from the <c>test plan:</c> engine-log lines and the run's summary, so a plan that selected nothing reads as
///     zeros rather than as missing.
/// </summary>
internal sealed partial record TestPlanCounts(
    int ProjectsPlanned,
    int ShadowRuns,
    int MsbuildRuns,
    int WholeProjectRuns,
    List<string> WholeProjectReasons,
    List<string> FilterCollapses,
    int TestProcesses)
{
    /// <summary>
    ///     Reads the counts for one test run from its console <paramref name="output"/> and the engine-log
    ///     <paramref name="lines"/> written since it started. A run that reports <c>no test is affected</c> records zeros
    ///     with no projects planned, even though the engine logged its empty selection.
    /// </summary>
    public static TestPlanCounts Parse(string output, IReadOnlyList<string> lines)
    {
        if (output.Contains("no test is affected", StringComparison.Ordinal))
            return new TestPlanCounts(0, 0, 0, 0, [], [], 0);
        var planned = new HashSet<string>(StringComparer.Ordinal);
        var modes = new List<(string Name, bool Msbuild, int Assemblies)>();
        var collapses = new List<string>();
        var whole = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("test plan: selection in ", StringComparison.Ordinal))
            {
                // test plan: selection in 12 ms: Core.Tests 3 pattern(s); Services.Tests all
                var tail = line[(line.IndexOf(" ms: ", StringComparison.Ordinal) + " ms: ".Length)..];
                foreach (var entry in tail.Split("; ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (entry.EndsWith("all", StringComparison.Ordinal))
                    {
                        planned.Add(entry[..^"all".Length].TrimEnd());
                        whole++;
                    }
                    else if (SelectionEntry().Match(entry) is { Success: true } selection)
                        planned.Add(selection.Groups[1].Value);
                }
            }
            else if (ModeLine().Match(line) is { Success: true } mode)
            {
                // test plan: Core.Tests [net10.0] runs from <assembly> after 34 ms, or builds with MSBuild.
                var name = mode.Groups[1].Value;
                planned.Add(name);
                var viaMsBuild = mode.Groups[3].Value == "builds with MSBuild";
                var assemblies = viaMsBuild ? 0 : mode.Groups[4].Value.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
                modes.Add((name, viaMsBuild, assemblies));
            }
            else if (line.StartsWith("test plan: ", StringComparison.Ordinal) && line.Contains("collapsed to", StringComparison.Ordinal))
            {
                collapses.Add(line);
                planned.Add(line["test plan: ".Length..line.IndexOf(" filter of ", StringComparison.Ordinal)]);
            }
            else if (line.StartsWith("test plan: ", StringComparison.Ordinal) && line.Contains(" runs whole: ", StringComparison.Ordinal))
            {
                planned.Add(line["test plan: ".Length..line.IndexOf(" runs whole: ", StringComparison.Ordinal)]);
            }
        }

        var reasons = WholeReasons().Match(output) is { Success: true } parenthetical
            ? parenthetical.Groups[1].Value.Split("; ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : new List<string>();
        var shadow = modes.Count(m => !m.Msbuild);
        var msbuild = modes.Count(m => m.Msbuild);
        // A project on Microsoft.Testing.Platform logs a mode line that builds with MSBuild, so MsbuildRuns counts it
        // with the VSTest projects that build the same way; what is left here is a planned project with no mode line,
        // counted as one process. The target multiplicity of a direct run is not in the log, so this counts the
        // processes the plan implies, not OS-observed forks.
        var withoutMode = planned.Except(modes.Select(m => m.Name), StringComparer.Ordinal).Count();
        return new TestPlanCounts(planned.Count, shadow, msbuild, whole, reasons, collapses, modes.Sum(m => m.Assemblies) + msbuild + withoutMode);
    }

    /// <summary>
    ///     How many tests the run selected, from its <c>ran N test(s) affected by your changes out of M</c> summary, or 0
    ///     when it reports <c>no test is affected</c>, or null when it never planned, as an unanswered run does.
    /// </summary>
    public static int? SelectedTests(string output)
    {
        if (SelectedSummary().Match(output) is { Success: true } selected)
            return int.Parse(selected.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return output.Contains("no test is affected", StringComparison.Ordinal) ? 0 : null;
    }

    /// <summary>Whether the run reports the changes compile to the same code as HEAD, selecting nothing for that reason.</summary>
    public static bool CompilesToSameCode(string output) =>
        output.Contains("compile to the same code as HEAD", StringComparison.Ordinal);

    /// <summary>
    ///     How many symbols the newest <c>test selection:</c> line since the run started says the member walk visited, or
    ///     null when the engine logged none, as a build without that line does.
    /// </summary>
    public static int? CandidatesBound(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].StartsWith("test selection:", StringComparison.Ordinal) && WalkSymbols().Match(lines[i]) is { Success: true } symbols)
                return int.Parse(symbols.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return null;
    }

    /// <summary>The failed, passed and skipped counts in a <c>fuse: F failed, P passed</c> summary line, or zeros without one.</summary>
    public static (int Failed, int Passed, int Skipped) RunCounts(string output)
    {
        var match = Counts().Match(output);
        if (!match.Success)
            return (0, 0, 0);
        var skipped = Skipped().Match(output);
        return (
            int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            skipped.Success ? int.Parse(skipped.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0);
    }

    [GeneratedRegex(@"^(.+) \d+ pattern\(s\)$", RegexOptions.CultureInvariant)]
    private static partial Regex SelectionEntry();

    [GeneratedRegex(@"^test plan: (.+?) \[(.*)\] (builds with MSBuild|runs from (.+?)) after [\d.]+ ms$", RegexOptions.CultureInvariant)]
    private static partial Regex ModeLine();

    [GeneratedRegex(@"\(whole projects where ([^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex WholeReasons();

    [GeneratedRegex(@"ran (\d+) test\(s\) affected by your changes out of (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SelectedSummary();

    [GeneratedRegex(@"(\d+) symbol\(s\)", RegexOptions.CultureInvariant)]
    private static partial Regex WalkSymbols();

    [GeneratedRegex(@"fuse: (\d+) failed, (\d+) passed", RegexOptions.CultureInvariant)]
    private static partial Regex Counts();

    [GeneratedRegex(@"fuse: \d+ failed, \d+ passed, (\d+) skipped", RegexOptions.CultureInvariant)]
    private static partial Regex Skipped();
}
