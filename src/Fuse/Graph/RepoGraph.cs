using Fuse.Dotnet;
using Fuse.Repo;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;

namespace Fuse.Graph;

/// <summary>
///     Every C# project in the repository, evaluated by MSBuild (imports, conditions and globs resolved, no targets
///     run). It answers which projects own a file and which projects depend on a project, without compiling anything.
/// </summary>
internal sealed class RepoGraph
{
    private static readonly string[] TestFrameworkPackages =
        ["xunit", "xunit.v3", "xunit.core", "NUnit", "MSTest", "MSTest.TestFramework", "Microsoft.Testing.Platform", "TUnit", "Microsoft.NET.Test.Sdk"];

    private readonly Dictionary<string, ProjectNode> _byPath;
    private readonly Dictionary<string, List<ProjectNode>> _owners;
    private readonly Dictionary<string, List<ProjectNode>> _directDependents;

    private RepoGraph(IReadOnlyList<ProjectNode> projects, IReadOnlyList<string> failures)
    {
        Projects = projects;
        Failures = failures;
        _byPath = projects.ToDictionary(p => p.Path, ChangeTracker.PathComparer);
        _owners = new Dictionary<string, List<ProjectNode>>(ChangeTracker.PathComparer);
        _directDependents = projects.ToDictionary(p => p.Path, _ => new List<ProjectNode>(), ChangeTracker.PathComparer);
        foreach (var project in projects)
        {
            foreach (var source in project.Sources)
            {
                if (!_owners.TryGetValue(source, out var list))
                    _owners[source] = list = [];
                list.Add(project);
            }

            foreach (var reference in project.References)
            {
                if (_directDependents.TryGetValue(reference, out var dependents))
                    dependents.Add(project);
            }
        }
    }

    public IReadOnlyList<ProjectNode> Projects { get; }

    /// <summary>One line per project that could not be evaluated.</summary>
    public IReadOnlyList<string> Failures { get; }

    public ProjectNode? Find(string projectPath) => _byPath.GetValueOrDefault(projectPath);

    /// <summary>The projects that compile <paramref name="path"/>. A file created after evaluation belongs to the deepest project directory containing it.</summary>
    public IReadOnlyList<ProjectNode> OwnersOf(string path)
    {
        if (_owners.TryGetValue(path, out var owners))
            return owners;
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
            return [];
        ProjectNode? best = null;
        foreach (var project in Projects)
        {
            var dir = project.Directory + System.IO.Path.DirectorySeparatorChar;
            if (path.StartsWith(dir, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && (best is null || project.Directory.Length > best.Directory.Length))
                best = project;
        }

        return best is null ? [] : [best];
    }

    /// <summary>Every project that references <paramref name="project"/>, directly or transitively.</summary>
    public IReadOnlyList<ProjectNode> DependentsOf(ProjectNode project)
    {
        var result = new List<ProjectNode>();
        var seen = new HashSet<string>(ChangeTracker.PathComparer) { project.Path };
        var queue = new Queue<ProjectNode>([project]);
        while (queue.Count > 0)
        {
            foreach (var dependent in _directDependents[queue.Dequeue().Path])
            {
                if (seen.Add(dependent.Path))
                {
                    result.Add(dependent);
                    queue.Enqueue(dependent);
                }
            }
        }

        return result;
    }

    /// <summary><paramref name="project"/> and every project it references, directly or transitively.</summary>
    public IReadOnlyList<ProjectNode> ClosureOf(ProjectNode project)
    {
        var result = new List<ProjectNode>();
        var seen = new HashSet<string>(ChangeTracker.PathComparer);
        var stack = new Stack<ProjectNode>([project]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!seen.Add(current.Path))
                continue;
            result.Add(current);
            foreach (var reference in current.References)
            {
                if (_byPath.TryGetValue(reference, out var node))
                    stack.Push(node);
            }
        }

        return result;
    }

    /// <summary>True when <paramref name="path"/> is an evaluation input of any project.</summary>
    public bool IsEvaluationInput(string path) => Projects.Any(p => p.EvaluationInputs.Contains(path));

    /// <summary>Evaluates every project file git knows about (tracked or untracked, not ignored).</summary>
    public static async Task<RepoGraph> EvaluateAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        var listing = await ProcessRunner.RunAsync(
            "git",
            ["-c", "core.quotepath=off", "ls-files", "--cached", "--others", "--exclude-standard", "--", "*.csproj"],
            root.Path,
            cancellationToken).ConfigureAwait(false);
        var paths = listing.ExitCode == 0
            ? listing.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => System.IO.Path.GetFullPath(System.IO.Path.Combine(root.Path, p)))
                .Where(File.Exists)
                .Distinct(ChangeTracker.PathComparer)
                .ToList()
            : [];

        MsBuildSetup.EnsureRegistered();
        return Evaluate(root, paths, cancellationToken);
    }

    private static RepoGraph Evaluate(RepoRoot root, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var projects = new List<ProjectNode>();
        var failures = new List<string>();
        using var collection = new ProjectCollection();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var project = collection.LoadProject(path);
                projects.Add(ToNode(root, project));
                collection.UnloadProject(project);
            }
            catch (InvalidProjectFileException e)
            {
                failures.Add($"{root.Relative(path)}: {e.BaseMessage}");
            }
        }

        return new RepoGraph(projects, failures);
    }

    private static ProjectNode ToNode(RepoRoot root, Project project)
    {
        var dir = project.DirectoryPath;
        string Full(string include) => System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, include));

        var sources = new HashSet<string>(ChangeTracker.PathComparer);
        foreach (var item in project.GetItems("Compile"))
            sources.Add(Full(item.EvaluatedInclude));
        foreach (var itemType in new[] { "Content", "None", "RazorComponent", "AdditionalFiles" })
        {
            foreach (var item in project.GetItems(itemType))
            {
                if (ChangeTracker.IsSource(item.EvaluatedInclude))
                    sources.Add(Full(item.EvaluatedInclude));
            }
        }

        var references = project.GetItems("ProjectReference").Select(i => Full(i.EvaluatedInclude)).Distinct(ChangeTracker.PathComparer).ToList();
        var packages = project.GetItems("PackageReference").Select(i => i.EvaluatedInclude).ToList();
        var isTest = project.GetPropertyValue("IsTestProject").Equals("true", StringComparison.OrdinalIgnoreCase)
                     || packages.Any(p => TestFrameworkPackages.Contains(p, StringComparer.OrdinalIgnoreCase));
        var isTestingPlatform = project.GetPropertyValue("IsTestingPlatformApplication").Equals("true", StringComparison.OrdinalIgnoreCase)
                                && !project.GetPropertyValue("UseMicrosoftTestingPlatformRunner").Equals("false", StringComparison.OrdinalIgnoreCase)
                                && GlobalJsonUsesTestingPlatform(root);

        var inputs = new HashSet<string>(ChangeTracker.PathComparer) { project.FullPath };
        foreach (var import in project.Imports)
        {
            var importPath = import.ImportedProject.FullPath;
            if (importPath.StartsWith(root.Path, StringComparison.OrdinalIgnoreCase)
                && !importPath.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                inputs.Add(importPath);
        }

        var assets = project.GetPropertyValue("ProjectAssetsFile");
        return new ProjectNode
        {
            Path = project.FullPath,
            Name = System.IO.Path.GetFileNameWithoutExtension(project.FullPath),
            Directory = dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
            References = references,
            Sources = sources,
            EvaluationInputs = inputs,
            AssetsFile = string.IsNullOrEmpty(assets) ? System.IO.Path.Combine(dir, "obj", "project.assets.json") : Full(assets),
            IsTest = isTest,
            IsTestingPlatform = isTestingPlatform,
        };
    }

    private static bool GlobalJsonUsesTestingPlatform(RepoRoot root)
    {
        var globalJson = System.IO.Path.Combine(root.Path, "global.json");
        return File.Exists(globalJson)
               && File.ReadAllText(globalJson).Contains("Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase);
    }
}
