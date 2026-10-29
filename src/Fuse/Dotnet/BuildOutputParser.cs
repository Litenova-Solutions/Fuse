using System.Globalization;
using System.Text.RegularExpressions;

namespace Fuse.Dotnet;

/// <summary>Extracts error lines from MSBuild console output and normalizes them to one line each, without the trailing project tag.</summary>
internal static partial class BuildOutputParser
{
    /// <summary>Distinct error lines in first-seen order, with paths made relative to <paramref name="root"/>.</summary>
    public static List<string> Errors(string output, string root) =>
        [.. Diagnostics(output, root).Select(d => d.ToString())];

    /// <summary>Distinct errors in first-seen order, with paths made relative to <paramref name="root"/>.</summary>
    public static List<BuildDiagnostic> Diagnostics(string output, string root)
    {
        var result = new List<BuildDiagnostic>();
        var seen = new HashSet<BuildDiagnostic>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            var match = FileDiagnostic().Match(line);
            BuildDiagnostic? diagnostic = null;
            if (match.Success && match.Groups["sev"].Value == "error")
            {
                // A line or column no int holds is no diagnostic MSBuild wrote; it is left out rather than breaking the answer.
                if (int.TryParse(match.Groups["line"].Value, CultureInfo.InvariantCulture, out var lineNumber)
                    && int.TryParse(match.Groups["col"].Value, CultureInfo.InvariantCulture, out var column))
                    diagnostic = new BuildDiagnostic(Relative(match.Groups["file"].Value.Trim(), root), lineNumber, column, match.Groups["id"].Value, match.Groups["msg"].Value.Trim());
            }
            else if (!match.Success)
            {
                var general = GeneralError().Match(line);
                if (general.Success)
                    diagnostic = new BuildDiagnostic(Relative(general.Groups["origin"].Value.Trim(), root), null, null, general.Groups["id"].Value, general.Groups["msg"].Value.Trim());
            }

            if (diagnostic is not null && seen.Add(diagnostic))
                result.Add(diagnostic);
        }

        return result;
    }

    internal static string Relative(string path, string root)
    {
        if (!Path.IsPathRooted(path))
            return path.Replace('\\', '/');
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
    }

    [GeneratedRegex(@"^(?<file>[^\r\n]+?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<id>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?$")]
    internal static partial Regex FileDiagnostic();

    [GeneratedRegex(@"^(?<origin>[^\r\n]*?)\s*:\s*error\s+(?<id>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?$")]
    private static partial Regex GeneralError();
}
