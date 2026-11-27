using System.Text;
using Fuse.Graph;
using Fuse.Paths;
namespace Fuse.Testing.Selection.ProjectFiles;

/// <summary>
///     The test projects a project-file change selects whole: a package version change selects the test projects
///     that reference the package, a project file change selects its project and its dependent test projects, and a
///     build settings file selects the test projects under its directory. A comment or whitespace edit selects nothing.
/// </summary>
internal static class ProjectFileSelection
{
    /// <summary>
    ///     The whole selections project-file changes call for, keyed by test project file with the reason each
    ///     project runs whole. Reads HEAD through <paramref name="readHead"/> and disk directly.
    /// </summary>
    public static IReadOnlyDictionary<RepoPath, string> Select(RepoGraph graph, Func<RepoPath, byte[]?> readHead, IReadOnlyList<RepoPath> changedFiles)
    {
        var selections = new Dictionary<RepoPath, string>();
        foreach (var path in changedFiles)
        {
            if (path.FileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
                SelectForPackagesProps(graph, readHead, path, selections);
            else if (path.Absolute.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                SelectForProject(graph, readHead, path, selections);
            else if (IsBuildSettings(path.FileName))
                SelectForBuildSettings(graph, readHead, path, selections);
        }

        return selections;
    }

    private static void SelectForPackagesProps(RepoGraph graph, Func<RepoPath, byte[]?> readHead, RepoPath path, Dictionary<RepoPath, string> selections)
    {
        var changed = PackageVersionChanges.ChangedPackages(Decode(readHead(path)), ReadDisk(path));
        if (changed is null)
        {
            foreach (var test in graph.Projects.Where(p => p.IsTest))
                selections.TryAdd(test.Path, "package versions changed");
            return;
        }

        foreach (var name in changed)
        {
            foreach (var test in graph.Projects.Where(p => p.IsTest && ReferencesPackage(graph, p, name)))
                selections.TryAdd(test.Path, "package " + name + " version changed");
        }
    }

    private static void SelectForProject(RepoGraph graph, Func<RepoPath, byte[]?> readHead, RepoPath path, Dictionary<RepoPath, string> selections)
    {
        if (!ProjectFileChanges.ProjectContentChanged(Decode(readHead(path)), ReadDisk(path)))
            return;

        var node = graph.Find(path);
        if (node is null)
        {
            foreach (var test in graph.Projects.Where(p => p.IsTest))
                selections.TryAdd(test.Path, path.FileName + " changed");
            return;
        }

        foreach (var test in HostRule.DependentTestProjects(graph, node))
            selections.TryAdd(test.Path, path.FileName + " changed");
    }

    private static void SelectForBuildSettings(RepoGraph graph, Func<RepoPath, byte[]?> readHead, RepoPath path, Dictionary<RepoPath, string> selections)
    {
        var headText = Decode(readHead(path));
        var diskText = ReadDisk(path);
        var changed = path.FileName.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            ? ProjectFileChanges.GlobalJsonChanged(headText, diskText)
            : ProjectFileChanges.BuildContentChanged(headText, diskText);
        if (!changed)
            return;

        var directory = Path.GetDirectoryName(path.Absolute) ?? "";
        foreach (var test in graph.Projects.Where(p => p.IsTest && IsUnder(directory, p.Path.Absolute)))
            selections.TryAdd(test.Path, path.FileName + " changed");
    }

    /// <summary>True when the test project <paramref name="test"/> references <paramref name="package"/> itself or through a project it references.</summary>
    private static bool ReferencesPackage(RepoGraph graph, ProjectNode test, string package)
    {
        foreach (var node in graph.ClosureOf(test))
        {
            if (node.PackageReferences.Contains(package))
                return true;
        }

        return false;
    }

    private static bool IsBuildSettings(string fileName) =>
        fileName.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="file"/> lies under the directory <paramref name="directory"/> names.</summary>
    private static bool IsUnder(string directory, string file)
    {
        var relative = Path.GetRelativePath(directory, file);
        return relative != ".."
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith("../", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static string? Decode(byte[]? bytes)
    {
        if (bytes is null)
            return null;
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string? ReadDisk(RepoPath path)
    {
        try
        {
            return File.Exists(path.Absolute) ? File.ReadAllText(path.Absolute) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
