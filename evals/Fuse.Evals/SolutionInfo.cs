using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Fuse.Evals;

/// <summary>The projects a solution builds, split into test and non-test, and the non-test C# sources that mutations may touch.</summary>
internal sealed partial class SolutionInfo
{
    private SolutionInfo(string root, List<string> projects)
    {
        Root = root;
        Projects = projects;
        TestProjects = projects.Where(IsTestProject).ToList();
        CodeProjects = projects.Except(TestProjects).ToList();
    }

    public string Root { get; }

    /// <summary>Absolute project file paths in the solution.</summary>
    public List<string> Projects { get; }

    public List<string> TestProjects { get; }

    public List<string> CodeProjects { get; }

    public static SolutionInfo Load(string root, string solution)
    {
        var path = Path.Combine(root, solution);
        var dir = Path.GetDirectoryName(path)!;
        IEnumerable<string> relative = path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? XDocument.Load(path).Descendants("Project").Select(p => (string?)p.Attribute("Path")).OfType<string>()
            : SlnProject().Matches(File.ReadAllText(path)).Select(m => m.Groups[1].Value);
        var projects = relative
            .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(dir, p.Replace('\\', Path.DirectorySeparatorChar))))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new SolutionInfo(root, projects);
    }

    /// <summary>C# files under the non-test project directories, excluding build output and generated code.</summary>
    public List<string> CodeSources()
    {
        var result = new List<string>();
        var projectDirs = Projects.Select(p => Path.GetDirectoryName(p)!).ToList();
        foreach (var project in CodeProjects)
        {
            var dir = Path.GetDirectoryName(project)!;
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file);
                var segments = rel.Split(Path.DirectorySeparatorChar);
                if (segments.Any(s => s is "bin" or "obj") || file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Skip files that belong to a project nested inside this one.
                if (projectDirs.Any(d => d.Length > dir.Length && file.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    continue;
                result.Add(file);
            }
        }

        return result;
    }

    /// <summary>The project directory a file belongs to (deepest containing project).</summary>
    public string? ProjectOf(string file) =>
        Projects.Select(p => Path.GetDirectoryName(p)!)
            .Where(d => file.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Length)
            .FirstOrDefault();

    /// <summary>Project files that reference <paramref name="project"/> directly.</summary>
    public int DependentCount(string project) =>
        Projects.Count(p => p != project && File.ReadAllText(p).Contains(Path.GetFileName(project), StringComparison.OrdinalIgnoreCase));

    /// <summary>Projects <paramref name="project"/> references, directly or transitively.</summary>
    public static HashSet<string> ReferencesOf(string project)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>([project]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (Match match in ProjectReference().Matches(File.ReadAllText(current)))
            {
                var referenced = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)));
                if (File.Exists(referenced) && result.Add(referenced))
                    stack.Push(referenced);
            }
        }

        return result;
    }

    /// <summary>The project file owning <paramref name="file"/> (a repository-relative or absolute path).</summary>
    public string? ProjectFileOf(string file)
    {
        var full = Path.GetFullPath(Path.Combine(Root, file));
        return Projects.Where(p => full.StartsWith(Path.GetDirectoryName(p)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Length)
            .FirstOrDefault();
    }

    private static bool IsTestProject(string project)
    {
        var text = File.ReadAllText(project);
        return text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
               || text.Contains("xunit", StringComparison.OrdinalIgnoreCase)
               || text.Contains("NUnit", StringComparison.OrdinalIgnoreCase)
               || text.Contains("MSTest", StringComparison.OrdinalIgnoreCase)
               || text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase)
               || Path.GetFileNameWithoutExtension(project).EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
               || Path.GetFileNameWithoutExtension(project).EndsWith(".Tests", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"<ProjectReference\s+Include=""([^""]+)""")]
    private static partial Regex ProjectReference();

    [GeneratedRegex(@"Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*"",\s*""([^""]+)""")]
    private static partial Regex SlnProject();
}
