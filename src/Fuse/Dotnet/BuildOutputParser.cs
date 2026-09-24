using System.Text.RegularExpressions;

namespace Fuse.Dotnet;

/// <summary>Extracts error lines from MSBuild console output and normalizes them to one line each, without the trailing project tag.</summary>
internal static partial class BuildOutputParser
{
    /// <summary>Distinct error lines in first-seen order, with paths made relative to <paramref name="root"/>.</summary>
    public static List<string> Errors(string output, string root)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            var match = FileDiagnostic().Match(line);
            string? normalized = null;
            if (match.Success && match.Groups["sev"].Value == "error")
            {
                normalized = $"{Relative(match.Groups["file"].Value.Trim(), root)}({match.Groups["line"].Value},{match.Groups["col"].Value}): error {match.Groups["id"].Value}: {match.Groups["msg"].Value.Trim()}";
            }
            else if (!match.Success)
            {
                var general = GeneralError().Match(line);
                if (general.Success)
                    normalized = $"{Relative(general.Groups["origin"].Value.Trim(), root)}: error {general.Groups["id"].Value}: {general.Groups["msg"].Value.Trim()}";
            }

            if (normalized is not null && seen.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }

    private static string Relative(string path, string root)
    {
        if (!Path.IsPathRooted(path))
            return path.Replace('\\', '/');
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative.Replace('\\', '/');
    }

    [GeneratedRegex(@"^(?<file>[^\r\n]+?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning)\s+(?<id>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?$")]
    private static partial Regex FileDiagnostic();

    [GeneratedRegex(@"^(?<origin>[^\r\n]*?)\s*:\s*error\s+(?<id>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[[^\]]+\])?$")]
    private static partial Regex GeneralError();
}
