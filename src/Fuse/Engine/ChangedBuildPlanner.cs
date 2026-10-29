using Fuse.Graph;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Repo;

namespace Fuse.Engine;

/// <summary>
///     Plans <c>fuse build --changed</c>: the projects that own the files changed since HEAD and their dependents,
///     reduced to the highest projects, because building one builds what it references. When the selection cannot be
///     trusted it names the whole solution instead, and why.
/// </summary>
internal static class ChangedBuildPlanner
{
    /// <summary>How many project files a fallback reason names before it counts the rest.</summary>
    private const int MaxNamedProjectFiles = 5;

    /// <param name="graph">The evaluated projects.</param>
    /// <param name="changed">The sources that differ from HEAD.</param>
    /// <param name="projectFilesChanged">The project files git reports as changed, whose owners are stale by definition.</param>
    public static ChangedBuildPlan Plan(RepoGraph graph, IReadOnlyCollection<RepoPath> changed, IReadOnlyCollection<RepoPath> projectFilesChanged)
    {
        var tree = TreeStamp.Of(ChangedPairs(changed));
        var owners = changed.SelectMany(graph.OwnersOf).DistinctBy(node => node.Path).ToList();
        var changedIn = owners.Select(node => node.Name).Order(StringComparer.Ordinal).ToArray();
        if (projectFilesChanged.Count > 0)
            return new ChangedBuildPlan([], changedIn, changed.Count, tree, ProjectFilesReason(projectFilesChanged), UnownedCount(graph, changed));
        if (owners.Count == 0)
        {
            if (changed.Count == 0)
                return new ChangedBuildPlan([], [], 0, tree);
            return new ChangedBuildPlan([], [], changed.Count, tree, $"{changed.Count} changed file(s) in no project", changed.Count);
        }

        var withDependents = owners.Concat(owners.SelectMany(graph.DependentsOf)).DistinctBy(node => node.Path).ToList();
        var closures = withDependents.ToDictionary(node => node.Path, node => graph.ClosureOf(node).Select(closed => closed.Path).ToHashSet());
        // A project another selected project references is built with it, so only the highest projects build.
        var reduced = withDependents
            .Where(node => !withDependents.Any(other => !other.Path.Equals(node.Path) && closures[other.Path].Contains(node.Path)))
            .OrderBy(node => node.Name, StringComparer.Ordinal)
            .ToList();
        var leaves = reduced.Count > 0 ? reduced : withDependents.OrderBy(node => node.Name, StringComparer.Ordinal).ToList();
        return new ChangedBuildPlan([.. leaves.Select(node => node.Path.Absolute)], changedIn, changed.Count, tree, "", UnownedCount(graph, changed));
    }

    /// <summary>How many changed files no project compiles.</summary>
    private static int UnownedCount(RepoGraph graph, IReadOnlyCollection<RepoPath> changed) =>
        changed.Count(path => graph.OwnersOf(path).Count == 0);

    private static string ProjectFilesReason(IReadOnlyCollection<RepoPath> projectFilesChanged)
    {
        var names = projectFilesChanged.Select(path => path.Relative).Order(StringComparer.Ordinal).ToList();
        var shown = string.Join(", ", names.Take(MaxNamedProjectFiles));
        var rest = names.Count - Math.Min(names.Count, MaxNamedProjectFiles);
        return $"project files changed ({shown}{(rest > 0 ? $", and {rest} more" : "")})";
    }

    private static IEnumerable<(string Relative, string Absolute)> ChangedPairs(IReadOnlyCollection<RepoPath> changed) =>
        changed.Select(path => (path.Relative, path.Absolute));
}
