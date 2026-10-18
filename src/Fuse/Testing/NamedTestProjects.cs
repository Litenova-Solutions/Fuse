using System.Xml.Linq;
using Fuse.Failures;
using Fuse.Graph;

namespace Fuse.Testing;

/// <summary>
///     The test projects absolute paths name, for a plan limited by <c>dotnet test</c> arguments that only said where
///     the tests are: a project file names itself, a solution file the projects it lists, and a directory every test
///     project under it.
/// </summary>
internal static class NamedTestProjects
{
    /// <exception cref="FuseException">None of <paramref name="paths"/> names a test project.</exception>
    public static IReadOnlyList<ProjectNode> Resolve(RepoGraph graph, IReadOnlyList<string> paths)
    {
        var named = new List<ProjectNode>();
        foreach (var path in paths)
        {
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                Add(graph, named, path);
            else if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var project in SolutionProjects(path))
                    Add(graph, named, project);
            }
            else
            {
                foreach (var node in graph.Projects.Where(p => p.IsTest && IsUnder(path, p.Path.Absolute)))
                {
                    if (!named.Contains(node))
                        named.Add(node);
                }
            }
        }

        return named.Count > 0
            ? named
            : throw new FuseException(ErrorCode.InvalidPath, ErrorMessages.NoTestProjectNamed(paths));
    }

    private static void Add(RepoGraph graph, List<ProjectNode> named, string projectPath)
    {
        var node = graph.Projects.FirstOrDefault(p => p.Path.Matches(projectPath));
        if (node is { IsTest: true } && !named.Contains(node))
            named.Add(node);
    }

    /// <summary>Whether <paramref name="file"/> lies under the directory <paramref name="directory"/> names.</summary>
    private static bool IsUnder(string directory, string file)
    {
        var relative = Path.GetRelativePath(directory, file);
        return relative != ".."
               && !relative.StartsWith("..\\", StringComparison.Ordinal)
               && !relative.StartsWith("../", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    /// <summary>The absolute project paths a solution file lists; none when the file cannot be read as one.</summary>
    private static List<string> SolutionProjects(string solution)
    {
        var directory = Path.GetDirectoryName(solution) ?? "";
        string Full(string relative) => Path.GetFullPath(Path.Combine(directory, relative.Replace('\\', Path.DirectorySeparatorChar)));
        try
        {
            if (solution.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                return XDocument.Load(solution).Descendants("Project")
                    .Select(p => p.Attribute("Path")?.Value)
                    .OfType<string>()
                    .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    .Select(Full)
                    .ToList();
            }

            return File.ReadLines(solution)
                .Where(l => l.StartsWith("Project(", StringComparison.Ordinal))
                .Select(l => l.Split(',').ElementAtOrDefault(1)?.Trim().Trim('"'))
                .OfType<string>()
                .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .Select(Full)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
    }
}
