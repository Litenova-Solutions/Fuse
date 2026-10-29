using System.Globalization;
using System.Text.RegularExpressions;

namespace Fuse.Dotnet;

/// <summary>
///     The file log Fuse adds to a build it runs: MSBuild's normal verbosity with its performance summary, in a file the
///     printed output never shows. It holds three things the console output with errors only leaves out: what a command
///     that exited with a code other than 0 printed, where the build's time went, and how many warnings it produced.
/// </summary>
/// <remarks>
///     The log is MSBuild's file logger number 9, so a file logger the caller adds with <c>-fl</c> stays the caller's. In a
///     build on several nodes, a line that starts with a node number, such as <c>3&gt;</c>, switches to that node, and the
///     lines after it without one belong to the same node.
/// </remarks>
internal static partial class BuildLog
{
    /// <summary>At most this many of a failed command's last lines are kept.</summary>
    public const int MaxCommandLines = 20;

    /// <summary>At most this many failed commands are read from one log.</summary>
    public const int MaxCommands = 3;

    /// <summary>How many slowest tasks and projects <see cref="Times"/> names.</summary>
    public const int SlowestShown = 3;

    /// <summary>The MSBuild arguments that write the log to <paramref name="path"/>.</summary>
    public static string[] Arguments(string path) =>
        ["-fl9", $"-flp9:LogFile={path};Verbosity=normal;PerformanceSummary;NoItemAndPropertyList;Encoding=UTF-8"];

    /// <summary>
    ///     Each command that exited with a code other than 0 (MSB3073), as its error message quotes it, with the last lines
    ///     it printed before it exited: everything its node logged between the line that echoes the command and the
    ///     error. A command whose echo is not found has no lines.
    /// </summary>
    public static List<(string Command, List<string> Lines)> FailedCommands(string log)
    {
        var entries = Entries(log);
        var result = new List<(string Command, List<string> Lines)>();
        for (var i = 0; i < entries.Count && result.Count < MaxCommands; i++)
        {
            var (node, text) = entries[i];
            // The summary MSBuild writes at the end repeats every error, without the node it came from.
            if (text.Trim() is "Build FAILED." or "Build succeeded.")
                break;
            var match = FailedCommand().Match(text);
            if (!match.Success)
                continue;
            var command = match.Groups["command"].Value.Trim();
            if (command.Length == 0 || result.Any(r => r.Command == command))
                continue;
            result.Add((command, LinesBefore(entries, i, node, command)));
        }

        return result;
    }

    /// <summary>
    ///     Where the build's time went, from MSBuild's performance summary: the slowest tasks, summed over every project,
    ///     and the slowest projects. Null when the log has no summary.
    /// </summary>
    /// <remarks>
    ///     A task's time is summed over every project, and projects build in parallel, so the sum can exceed the build's own
    ///     time. The compiler task, <c>Csc</c>, includes the analyzers and source generators it runs.
    /// </remarks>
    public static string? Times(string log)
    {
        var tasks = Section(log, "Task Performance Summary:")
            .OrderByDescending(e => e.Ms)
            .Take(SlowestShown)
            .Select(e => $"{e.Name} {Seconds(e.Ms)}")
            .ToList();
        // The section nests each project's targets under it; MSBuild right-aligns the times, so the indent does not tell
        // them apart, but only a project's line names a project file.
        var projects = Section(log, "Project Performance Summary:")
            .Where(e => IsProject(e.Name))
            .OrderByDescending(e => e.Ms)
            .Take(SlowestShown)
            .Select(e => $"{ProjectName(e.Name)} {Seconds(e.Ms)}")
            .ToList();
        if (tasks.Count == 0 && projects.Count == 0)
            return null;
        var parts = new List<string>();
        if (tasks.Count > 0)
            parts.Add($"slowest tasks summed over projects: {string.Join(", ", tasks)}");
        if (projects.Count > 0)
            parts.Add($"slowest projects: {string.Join(", ", projects)}");
        return string.Join("; ", parts);
    }

    /// <summary>
    ///     How many warnings the build produced, from the count MSBuild writes in the summary at the end of the log.
    ///     Zero when the log has no summary, as for a command line MSBuild refused.
    /// </summary>
    public static int Warnings(string log) =>
        WarningCount().Matches(log) is { Count: > 0 } matches
            ? int.Parse(matches[^1].Groups["count"].Value, CultureInfo.InvariantCulture)
            : 0;

    /// <summary>
    ///     The warnings the log holds, distinct in first-seen order, with paths made relative to <paramref name="root"/>.
    ///     The console runs with errors only, so the file log is where the warnings are.
    /// </summary>
    public static List<BuildWarning> WarningsList(string log, string root)
    {
        var result = new List<BuildWarning>();
        var seen = new HashSet<BuildWarning>();
        foreach (var (_, text) in Entries(log))
        {
            var match = BuildOutputParser.FileDiagnostic().Match(text.Trim());
            if (!match.Success || match.Groups["sev"].Value != "warning")
                continue;
            if (!int.TryParse(match.Groups["line"].Value, CultureInfo.InvariantCulture, out var line)
                || !int.TryParse(match.Groups["col"].Value, CultureInfo.InvariantCulture, out var column))
                continue;
            var warning = new BuildWarning(BuildOutputParser.Relative(match.Groups["file"].Value.Trim(), root), line, column, match.Groups["id"].Value, match.Groups["msg"].Value.Trim());
            if (seen.Add(warning))
                result.Add(warning);
        }

        return result;
    }

    private static string Seconds(long ms) => (ms / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";

    /// <summary>A project file's name without its folder or extension, whichever separator the build's operating system used.</summary>
    private static string ProjectName(string path)
    {
        var name = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static bool IsProject(string name) =>
        name.EndsWith("proj", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".metaproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>The lines of the log, each with the node it belongs to and its text without the node number.</summary>
    private static List<(int Node, string Text)> Entries(string log)
    {
        var entries = new List<(int Node, string Text)>();
        var node = 0;
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var prefix = NodePrefix().Match(line);
            if (prefix.Success)
            {
                node = int.Parse(prefix.Groups["node"].Value, CultureInfo.InvariantCulture);
                line = line[prefix.Length..];
            }

            entries.Add((node, line));
        }

        return entries;
    }

    /// <summary>
    ///     The last lines <paramref name="node"/> logged between the echo of <paramref name="command"/> and the error at
    ///     <paramref name="error"/>, or none when the echo is not found.
    /// </summary>
    private static List<string> LinesBefore(List<(int Node, string Text)> entries, int error, int node, string command)
    {
        var lines = new List<string>();
        for (var j = error - 1; j >= 0; j--)
        {
            var (lineNode, text) = entries[j];
            if (lineNode != node)
                continue;
            var trimmed = text.Trim();
            if (trimmed == command)
            {
                lines.Reverse();
                return [.. lines.TakeLast(MaxCommandLines)];
            }

            if (trimmed.Length > 0)
                lines.Add(trimmed);
        }

        return [];
    }

    /// <summary>The entries of one performance summary section: each line's time and name, up to the blank line that ends it.</summary>
    private static IEnumerable<(long Ms, string Name)> Section(string log, string header)
    {
        var inSection = false;
        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!inSection)
            {
                inSection = line.Trim() == header;
                continue;
            }

            if (line.Trim().Length == 0)
                yield break;
            var match = SummaryLine().Match(line);
            if (match.Success)
                yield return (long.Parse(match.Groups["ms"].Value, CultureInfo.InvariantCulture), match.Groups["name"].Value.Trim());
        }
    }

    [GeneratedRegex(@"^\s*(?<node>\d+)(?::\d+)?>")]
    private static partial Regex NodePrefix();

    // The command can hold quotes of its own, so it runs to the last quote before "exited with code"; a command of several
    // lines has its first line on the error's line, and the echo's first line is compared with it.
    [GeneratedRegex(@"error MSB3073: The command ""(?<command>(?:.*(?="" exited with code))|[^\r\n]*)")]
    private static partial Regex FailedCommand();

    [GeneratedRegex(@"^\s+(?<ms>\d+) ms  (?<name>.+?)\s+\d+ calls$")]
    private static partial Regex SummaryLine();

    [GeneratedRegex(@"^\s*(?<count>\d+) Warning\(s\)\s*$", RegexOptions.Multiline)]
    private static partial Regex WarningCount();
}
