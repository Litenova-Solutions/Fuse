using System.Text;
using System.Text.RegularExpressions;

namespace Fuse.Languages.CSharp.Maps;

/// <summary>
///     Parses .sln and .csproj ProjectReference elements into a dependency table.
/// </summary>
public sealed class CSharpProjectGraphGenerator : Fuse.Languages.Abstractions.Maps.IProjectGraphGenerator
{
    private static readonly Regex SolutionProjectRegex = new(
        @"Project\(""\{[A-F0-9-]+\}""\)\s*=\s*""(?<name>[^""]+)"",\s*""(?<path>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ProjectReferenceRegex = new(
        @"<ProjectReference\s+Include=""(?<path>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".sln", ".csproj"];

    /// <inheritdoc />
    public string Generate(IReadOnlyDictionary<string, string> fileContents)
    {
        var edges = new List<string>();

        foreach (var (path, content) in fileContents)
        {
            if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match match in SolutionProjectRegex.Matches(content))
                {
                    var name = match.Groups["name"].Value;
                    var projectPath = match.Groups["path"].Value.Replace('\\', '/');
                    edges.Add($"solution -> {name} ({projectPath})");
                }

                continue;
            }

            if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;

            var projectName = Path.GetFileNameWithoutExtension(path);
            foreach (Match match in ProjectReferenceRegex.Matches(content))
            {
                var referencePath = match.Groups["path"].Value.Replace('\\', '/');
                var referenceName = Path.GetFileNameWithoutExtension(referencePath);
                edges.Add($"{projectName} -> {referenceName}");
            }
        }

        if (edges.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<!-- fuse:project-graph");
        foreach (var edge in edges.OrderBy(e => e, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine(edge);
        sb.AppendLine("-->");
        return sb.ToString();
    }
}
