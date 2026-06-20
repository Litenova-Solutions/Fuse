using System.Text;
using System.Text.RegularExpressions;

namespace Fuse.Plugins.Languages.CSharp.Maps;

/// <summary>
///     Parses <c>.sln</c> project entries and <c>.csproj</c> <c>ProjectReference</c> elements into a project
///     dependency table.
/// </summary>
/// <remarks>
///     Edges are recovered by regex over the raw file text rather than by loading the MSBuild graph, so
///     references injected through imported targets, variables, or globbing are not resolved. Edges are
///     emitted in case-insensitive order inside a <c>&lt;!-- fuse:project-graph --&gt;</c> comment block.
/// </remarks>
public sealed class CSharpProjectGraphGenerator : Fuse.Plugins.Abstractions.Maps.IProjectGraphGenerator
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
