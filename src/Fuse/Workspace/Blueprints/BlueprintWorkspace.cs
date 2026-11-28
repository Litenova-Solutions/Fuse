using Fuse.Graph;
using Fuse.Paths;
using Fuse.Workspace.Blueprints.Projects;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
namespace Fuse.Workspace.Blueprints;

/// <summary>
///     Hosts projects loaded from a <see cref="CompilationBlueprint"/> inside a lean <see cref="AdhocWorkspace"/>
///     instead of keeping an <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/> resident in memory. Supports
///     shared immutable metadata/analyzer/HEAD-text caches across worktrees and true single-project unloading via
///     <see cref="UnloadProjects"/>.
/// </summary>
internal sealed class BlueprintWorkspace : IDisposable
{
    private readonly AdhocWorkspace _workspace;
    private readonly BlueprintResourceCache _shared;
    private readonly BlueprintProjectBuilder _builder;
    private readonly Dictionary<(RepoPath ProjectPath, string ProjectName), ProjectId> _projectIds = [];
    private readonly Dictionary<RepoPath, List<ProjectId>> _projectsByPath = [];

    public BlueprintWorkspace(BlueprintResourceCache? shared = null)
    {
        _workspace = new AdhocWorkspace();
        var loader = _workspace.Services.GetRequiredService<IAnalyzerService>().GetLoader();
        _shared = shared ?? new BlueprintResourceCache(loader);
        _builder = new BlueprintProjectBuilder(_shared);
    }

    /// <summary>Shared immutable resources (metadata references, analyzers, HEAD source texts) reusable across worktrees.</summary>
    public BlueprintResourceCache Shared => _shared;

    /// <summary>The current immutable solution snapshot held by this workspace.</summary>
    public Solution Solution => _workspace.CurrentSolution;

    /// <summary>True when at least one Roslyn project for <paramref name="projectPath"/> is loaded.</summary>
    public bool IsLoaded(RepoPath projectPath) =>
        _projectsByPath.TryGetValue(projectPath, out var ids) && ids.Count > 0;

    /// <summary>Every project file path currently open in this workspace.</summary>
    public List<RepoPath> LoadedPaths() => [.. _projectsByPath.Keys];

    /// <summary>
    ///     Loads <paramref name="targets"/> and their transitive dependencies from <paramref name="blueprint"/> into
    ///     the <see cref="AdhocWorkspace"/>, resolving all paths relative to <paramref name="targetRoot"/>.
    /// </summary>
    public void LoadProjects(
        RepoRoot targetRoot,
        RepoGraph graph,
        CompilationBlueprint blueprint,
        IReadOnlyCollection<ProjectNode> targets,
        string? head,
        IReadOnlySet<RepoPath>? dirtyPaths = null)
    {
        var closure = ExpandClosure(graph, targets);
        var byPath = blueprint.Projects
            .GroupBy(p => targetRoot.PathOf(CompilationBlueprint.ResolvePath(targetRoot, p.Identity.ProjectPath)))
            .ToDictionary(g => g.Key, g => g.ToList());

        var wanted = new List<(RepoPath Path, ProjectNode Node, CompilationBlueprint.BlueprintProject Blueprint)>();
        foreach (var node in closure)
        {
            if (!byPath.TryGetValue(node.Path, out var variants))
                continue;
            foreach (var variant in variants)
            {
                if (!_projectIds.ContainsKey((node.Path, variant.Identity.Name)))
                    wanted.Add((node.Path, node, variant));
            }
        }

        if (wanted.Count == 0)
            return;

        var ordered = OrderTopologically(targetRoot, wanted);
        var pendingIds = new Dictionary<(RepoPath Path, string Name), ProjectId>(_projectIds);
        foreach (var (path, _, bp) in ordered)
            pendingIds[(path, bp.Identity.Name)] = ProjectId.CreateNewId(debugName: bp.Identity.Name);

        var knownByPath = KnownSourcesByPath(targetRoot, byPath, blueprint);
        var infos = new List<ProjectInfo>(ordered.Count);
        var addedByPath = new Dictionary<RepoPath, List<(string Name, ProjectId Id)>>();
        foreach (var (path, node, bp) in ordered)
        {
            var id = pendingIds[(path, bp.Identity.Name)];
            var info = _builder.BuildProjectInfo(id, targetRoot, node, bp, blueprint.SourceRoot, pendingIds, head, dirtyPaths, knownByPath[path], _projectsByPath);
            infos.Add(info);
            if (!addedByPath.TryGetValue(path, out var list))
                addedByPath[path] = list = [];
            list.Add((bp.Identity.Name, id));
        }

        var next = AddProjectsToSolution(_workspace.CurrentSolution, infos);
        if (!_workspace.TryApplyChanges(next))
            throw new InvalidOperationException("could not apply blueprint projects to AdhocWorkspace");

        foreach (var (path, items) in addedByPath)
        {
            if (!_projectsByPath.TryGetValue(path, out var ids))
                _projectsByPath[path] = ids = [];
            foreach (var (name, id) in items)
            {
                _projectIds[(path, name)] = id;
                ids.Add(id);
            }
        }
    }

    /// <summary>
    ///     Adds pre-built <see cref="ProjectInfo"/> instances (such as from an exotic MSBuild fallback) directly into
    ///     the <see cref="AdhocWorkspace"/> so all projects remain in one solution with single-project unload support.
    /// </summary>
    public void AddExtractedProjects(RepoRoot root, IEnumerable<ProjectInfo> projects)
    {
        var list = projects.ToList();
        if (list.Count == 0)
            return;
        var next = AddProjectsToSolution(_workspace.CurrentSolution, list);
        if (!_workspace.TryApplyChanges(next))
            throw new InvalidOperationException("could not apply fallback projects to AdhocWorkspace");

        foreach (var project in list)
        {
            if (string.IsNullOrEmpty(project.FilePath))
                continue;
            var path = root.PathOf(project.FilePath);
            _projectIds[(path, project.Name)] = project.Id;
            if (!_projectsByPath.TryGetValue(path, out var ids))
                _projectsByPath[path] = ids = [];
            if (!ids.Contains(project.Id))
                ids.Add(project.Id);
        }
    }

    /// <summary>
    ///     Unloads every Roslyn project belonging to <paramref name="projectPaths"/> from the underlying
    ///     <see cref="AdhocWorkspace"/>, releasing its syntax trees, semantic models and compilations. Projects still
    ///     referenced by a non-unloaded project stay loaded so the solution graph remains valid.
    /// </summary>
    public IReadOnlyList<RepoPath> UnloadProjects(IEnumerable<RepoPath> projectPaths)
    {
        var requested = new HashSet<RepoPath>(projectPaths);
        if (requested.Count == 0)
            return [];

        var solution = _workspace.CurrentSolution;
        var candidateIds = new HashSet<ProjectId>();
        foreach (var path in requested)
        {
            if (_projectsByPath.TryGetValue(path, out var ids))
                candidateIds.UnionWith(ids);
        }

        if (candidateIds.Count == 0)
            return [];

        // Keep any project still referenced by a remaining project so Roslyn never holds dangling ProjectReferences.
        bool changed;
        do
        {
            changed = false;
            foreach (var project in solution.Projects)
            {
                if (candidateIds.Contains(project.Id))
                    continue;
                foreach (var reference in project.ProjectReferences)
                {
                    if (candidateIds.Remove(reference.ProjectId))
                        changed = true;
                }
            }
        }
        while (changed);

        if (candidateIds.Count == 0)
            return [];

        var next = solution;
        foreach (var id in candidateIds)
        {
            if (next.GetProject(id) is not null)
                next = next.RemoveProject(id);
        }

        if (!_workspace.TryApplyChanges(next))
            return [];

        var unloaded = new List<RepoPath>();
        foreach (var path in requested)
        {
            if (!_projectsByPath.TryGetValue(path, out var ids))
                continue;
            ids.RemoveAll(candidateIds.Contains);
            foreach (var key in _projectIds.Where(kv => kv.Key.ProjectPath == path && candidateIds.Contains(kv.Value)).Select(kv => kv.Key).ToList())
                _projectIds.Remove(key);
            if (ids.Count == 0)
            {
                _projectsByPath.Remove(path);
                unloaded.Add(path);
            }
        }

        return unloaded;
    }

    /// <summary>Clears every project from the workspace and resets project tracking.</summary>
    public void Reset()
    {
        _workspace.ClearSolution();
        _projectIds.Clear();
        _projectsByPath.Clear();
    }

    /// <summary>Drops transient HEAD text caches and metadata references no longer held by any live project.</summary>
    public void TrimCaches() => _shared.TrimUnused(_workspace.CurrentSolution);

    /// <summary>
    ///     The sources MSBuild evaluated at extraction time for each project path in <paramref name="byPath"/>, as
    ///     repository paths under <paramref name="targetRoot"/>. Paths a blueprint from before the extraction
    ///     recorded them fall back to the union of their variants' compile lists.
    /// </summary>
    private static Dictionary<RepoPath, HashSet<RepoPath>> KnownSourcesByPath(
        RepoRoot targetRoot,
        Dictionary<RepoPath, List<CompilationBlueprint.BlueprintProject>> byPath,
        CompilationBlueprint blueprint)
    {
        var result = new Dictionary<RepoPath, HashSet<RepoPath>>(byPath.Count);
        foreach (var (path, variants) in byPath)
        {
            var known = new HashSet<RepoPath>();
            var recorded = false;
            foreach (var variant in variants)
            {
                if (blueprint.KnownSources.TryGetValue(variant.Identity.ProjectPath, out var sources))
                {
                    recorded = true;
                    foreach (var relative in sources)
                        known.Add(targetRoot.PathOf(relative));
                }
            }

            if (!recorded)
            {
                foreach (var variant in variants)
                {
                    foreach (var doc in variant.Paths.Compile)
                        known.Add(targetRoot.PathOf(CompilationBlueprint.ResolvePath(targetRoot, doc.Path)));
                }
            }

            result[path] = known;
        }

        return result;
    }

    private static Solution AddProjectsToSolution(Solution solution, IEnumerable<ProjectInfo> projects)
    {
        var next = solution;
        foreach (var info in projects)
            next = next.AddProject(info);
        return next;
    }

    private static List<ProjectNode> ExpandClosure(RepoGraph graph, IEnumerable<ProjectNode> targets)
    {
        var seen = new HashSet<RepoPath>();
        var list = new List<ProjectNode>();
        foreach (var target in targets)
        {
            foreach (var node in graph.ClosureOf(target))
            {
                if (seen.Add(node.Path))
                    list.Add(node);
            }
        }

        return list;
    }

    private static List<(RepoPath Path, ProjectNode Node, CompilationBlueprint.BlueprintProject Blueprint)> OrderTopologically(
        RepoRoot root,
        List<(RepoPath Path, ProjectNode Node, CompilationBlueprint.BlueprintProject Blueprint)> items)
    {
        var byKey = items.ToDictionary(i => (i.Path, i.Blueprint.Identity.Name));
        var visited = new HashSet<(RepoPath, string)>();
        var ordered = new List<(RepoPath, ProjectNode, CompilationBlueprint.BlueprintProject)>(items.Count);

        void Visit((RepoPath Path, ProjectNode Node, CompilationBlueprint.BlueprintProject Blueprint) item)
        {
            var key = (item.Path, item.Blueprint.Identity.Name);
            if (!visited.Add(key))
                return;
            foreach (var pr in item.Blueprint.References.Items)
            {
                var targetPath = root.PathOf(CompilationBlueprint.ResolvePath(root, pr.TargetProjectPath));
                if (byKey.TryGetValue((targetPath, pr.TargetProjectName), out var dep))
                    Visit(dep);
            }

            ordered.Add(item);
        }

        foreach (var item in items)
            Visit(item);
        return ordered;
    }

    public void Dispose() => _workspace.Dispose();
}
