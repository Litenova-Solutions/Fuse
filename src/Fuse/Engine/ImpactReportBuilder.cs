using Fuse.Check;
using Fuse.Check.Model;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Protocol;
using Fuse.Testing;
using Fuse.Workspace.Generations;
namespace Fuse.Engine;

/// <summary>Builds the project, candidate file, and affected test report returned by <c>fuse impact</c>.</summary>
internal static class ImpactReportBuilder
{
    /// <summary>Reports the reach and test selection for the named paths, or for every changed file when none are named.</summary>
    public static async Task<ImpactReport> BuildAsync(
        RepoWorkspace workspace,
        Generation generation,
        string[] paths,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = RequestedPaths(workspace, generation, paths);
        if (requested.Length == 0)
            return new ImpactReport(Summary: SummaryOf(0, 0));

        var broadPath = requested.Any(IsBroadPath);
        var repositoryWide = requested.Any(IsRepositoryWidePath);
        var owners = OwnersOf(generation.Graph, requested);
        var reached = ReachedProjects(generation.Graph, owners, repositoryWide);
        if (!broadPath && reached.Count > 0)
            generation = await workspace.EnsureLoadedAndPinAsync(reached, cancellationToken).ConfigureAwait(false);

        var graph = generation.Graph;
        owners = OwnersOf(graph, requested);
        reached = ReachedProjects(graph, owners, repositoryWide);
        var reach = broadPath
            ? null
            : await new ChangeReach(workspace).ReachAsync(requested, reached, generation, cancellationToken).ConfigureAwait(false);
        var (selection, _) = await new TestSelector(workspace, TimeProvider.System)
            .SelectAsync(requested, generation, cancellationToken).ConfigureAwait(false);

        var affectedFiles = broadPath
            ? RelativeFiles(reached.SelectMany(project => project.Sources))
            : RelativeFiles(FilesOf(reach));
        var affectedProjects = NamesOf(reached);
        var affectedTests = broadPath
            ? NamesOf(reached.Where(project => project.IsTest))
            : NamesOf(selection.Selections.Keys.Select(graph.Find).OfType<ProjectNode>());
        var broadReach = broadPath || reach is Reach.Broad;
        return new ImpactReport(
            InputPaths: [.. requested.Select(path => path.Relative)],
            OwningProjects: NamesOf(owners),
            AffectedProjects: affectedProjects,
            AffectedFiles: affectedFiles,
            AffectedTestProjects: affectedTests,
            BroadReach: broadReach,
            Summary: SummaryOf(affectedProjects.Length, affectedTests.Length));
    }

    private static RepoPath[] RequestedPaths(RepoWorkspace workspace, Generation generation, string[] paths)
    {
        var requested = paths.Length == 0
            ? generation.Changed.Concat(generation.NonSources)
            : paths.Select(workspace.Root.PathOfNamed);
        return [.. requested.Distinct().OrderBy(path => path.Relative, StringComparer.Ordinal)];
    }

    private static List<ProjectNode> OwnersOf(RepoGraph graph, IReadOnlyList<RepoPath> paths) =>
        [.. paths.SelectMany(path => OwnersOf(graph, path)).DistinctBy(project => project.Path)
            .OrderBy(project => project.Path.Relative, StringComparer.Ordinal)];

    private static IReadOnlyList<ProjectNode> OwnersOf(RepoGraph graph, RepoPath path)
    {
        if (PathRules.IsSource(path.Absolute))
            return graph.OwnersOf(path);
        if (PathRules.IsProjectFile(path.Absolute))
            return [.. graph.Projects.Where(project => project.EvaluationInputs.Contains(path))];
        if (graph.NonSourceOwnersOf(path).Count > 0)
            return graph.NonSourceOwnersOf(path);
        return graph.ContainingProject(path) is { } project ? [project] : [];
    }

    private static List<ProjectNode> ReachedProjects(RepoGraph graph, IReadOnlyList<ProjectNode> owners, bool repositoryWide)
    {
        if (repositoryWide)
            return [.. graph.Projects.OrderBy(project => project.Path.Relative, StringComparer.Ordinal)];
        return [.. owners.Concat(owners.SelectMany(graph.DependentsOf)).DistinctBy(project => project.Path)
            .OrderBy(project => project.Path.Relative, StringComparer.Ordinal)];
    }

    private static IEnumerable<RepoPath> FilesOf(Reach? reach) => reach switch
    {
        Reach.Broad broad => broad.Files,
        Reach.Precise precise => precise.Causes.Keys,
        _ => [],
    };

    private static string[] RelativeFiles(IEnumerable<RepoPath> paths) =>
        [.. paths.Select(path => path.Relative).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static string[] NamesOf(IEnumerable<ProjectNode> projects) =>
        [.. projects.OrderBy(project => project.Path.Relative, StringComparer.Ordinal).Select(project => project.Name)];

    private static bool IsBroadPath(RepoPath path) => PathRules.IsProjectFile(path.Absolute) || IsSolution(path.Absolute);

    private static bool IsRepositoryWidePath(RepoPath path)
    {
        if (IsSolution(path.Absolute))
            return true;
        var name = Path.GetFileName(path.Absolute);
        return name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
               || name.Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSolution(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase);

    private static string SummaryOf(int projects, int tests) =>
        $"{projects} affected project(s), {tests} affected test project(s)";
}
