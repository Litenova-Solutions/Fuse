using Fuse.Graph;
using Fuse.Paths;
using Fuse.Repo.Tracking;
using Fuse.Workspace.Generations;
namespace Fuse.Inspection;

/// <summary>Builds the project graph, coupling clusters, and test ownership reported by <c>fuse graph</c>.</summary>
internal static class GraphAnalysis
{
    /// <summary>Builds a deterministic report from the graph in <paramref name="generation"/>.</summary>
    public static ProjectGraph Build(Generation generation)
    {
        var graph = generation.Graph;
        var projects = graph.Projects
            .OrderBy(project => project.Path.Relative, StringComparer.Ordinal)
            .Select(project => ReportProject(graph, project))
            .ToArray();
        var owners = graph.Projects
            .Where(project => project.IsTest)
            .OrderBy(project => project.Path.Relative, StringComparer.Ordinal)
            .Select(project => new TestCoverage(
                project.Name,
                NamesOf(graph.ClosureOf(project).Where(covered => !covered.IsTest))))
            .ToArray();

        return new ProjectGraph(
            TreeStamp.Of(generation.Changed.Select(path => (path.Relative, path.Absolute))),
            projects,
            CouplingClusters(graph),
            owners);
    }

    private static GraphProject ReportProject(RepoGraph graph, ProjectNode project) =>
        new(
            project.Name,
            project.Path.Relative,
            NamesOf(project.References.Select(graph.Find).OfType<ProjectNode>()),
            NamesOf(graph.Projects.Where(other => other.References.Contains(project.Path))),
            project.IsTest,
            Files: NamesOf(project.Sources));

    private static string[] NamesOf(IEnumerable<ProjectNode> projects) =>
        [.. projects.OrderBy(project => project.Path.Relative, StringComparer.Ordinal).Select(project => project.Name)];

    private static string[] NamesOf(IEnumerable<RepoPath> paths) =>
        [.. paths.OrderBy(path => path.Relative, StringComparer.Ordinal).Select(path => path.Relative)];

    private static ProjectCluster[] CouplingClusters(RepoGraph graph)
    {
        var projects = graph.Projects.Where(project => !project.IsTest).ToList();
        var byPath = projects.ToDictionary(project => project.Path);
        var adjacent = projects.ToDictionary(project => project.Path, _ => new HashSet<RepoPath>());
        var dependentsByReference = new Dictionary<RepoPath, List<ProjectNode>>();
        foreach (var project in projects)
        {
            foreach (var reference in project.References)
            {
                if (!byPath.TryGetValue(reference, out var dependency))
                    continue;
                adjacent[project.Path].Add(dependency.Path);
                adjacent[dependency.Path].Add(project.Path);
                if (!dependentsByReference.TryGetValue(reference, out var dependents))
                    dependentsByReference[reference] = dependents = [];
                dependents.Add(project);
            }
        }

        // Projects that reference the same dependency cannot be sliced independently of one another.
        foreach (var dependents in dependentsByReference.Values.Where(group => group.Count > 1))
        {
            var first = dependents[0];
            foreach (var other in dependents.Skip(1))
            {
                adjacent[first.Path].Add(other.Path);
                adjacent[other.Path].Add(first.Path);
            }
        }

        var visited = new HashSet<RepoPath>();
        var clusters = new List<ProjectCluster>();
        foreach (var project in projects.OrderBy(item => item.Path.Relative, StringComparer.Ordinal))
        {
            if (!visited.Add(project.Path))
                continue;
            var component = new List<ProjectNode>();
            var queue = new Queue<ProjectNode>([project]);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                component.Add(current);
                foreach (var neighbor in adjacent[current.Path].Where(visited.Add))
                    queue.Enqueue(byPath[neighbor]);
            }

            if (component.Count < 2)
                continue;
            component.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path.Relative, right.Path.Relative));
            var shared = component
                .SelectMany(item => item.References)
                .GroupBy(path => path)
                .Where(group => group.Count() > 1)
                .Select(group => graph.Find(group.Key))
                .OfType<ProjectNode>()
                .OrderBy(item => item.Path.Relative, StringComparer.Ordinal)
                .Select(item => item.Name)
                .ToArray();
            clusters.Add(new ProjectCluster(
                string.Join('+', component.Select(item => item.Name)),
                NamesOf(component),
                shared));
        }

        return [.. clusters];
    }
}
