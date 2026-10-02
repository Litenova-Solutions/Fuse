using Fuse.Dotnet;
using Fuse.Failures;
using Fuse.Paths;
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

    private readonly Dictionary<RepoPath, ProjectNode> _byPath;
    private readonly Dictionary<RepoPath, List<ProjectNode>> _owners;
    private readonly Dictionary<RepoPath, List<ProjectNode>> _directDependents;

    private RepoGraph(IReadOnlyList<ProjectNode> projects, IReadOnlyList<string> failures)
    {
        Projects = projects;
        Failures = failures;
        _byPath = projects.ToDictionary(p => p.Path);
        _owners = [];
        _directDependents = projects.ToDictionary(p => p.Path, _ => new List<ProjectNode>());
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

    /// <summary>The project whose file is <paramref name="projectPath"/>, or null when no evaluated project has it.</summary>
    public ProjectNode? Find(RepoPath projectPath) => _byPath.GetValueOrDefault(projectPath);

    /// <summary>The projects that compile <paramref name="path"/>. A file created after evaluation belongs to the deepest project directory containing it.</summary>
    public IReadOnlyList<ProjectNode> OwnersOf(RepoPath path)
    {
        if (_owners.TryGetValue(path, out var owners))
            return owners;
        if (!PathRules.IsSource(path.Absolute))
            return [];
        ProjectNode? best = null;
        foreach (var project in Projects)
        {
            if (path.IsUnder(project.Directory) && (best is null || project.Directory.Absolute.Length > best.Directory.Absolute.Length))
                best = project;
        }

        // A new file belongs to the deepest project directory holding it, unless it sits in build output.
        return best is null || PathRules.IsBuildOutput(best.Directory.Absolute, path.Absolute) ? [] : [best];
    }

    /// <summary>Every project that references <paramref name="project"/>, directly or transitively.</summary>
    public IReadOnlyList<ProjectNode> DependentsOf(ProjectNode project)
    {
        var result = new List<ProjectNode>();
        var seen = new HashSet<RepoPath> { project.Path };
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
        var seen = new HashSet<RepoPath>();
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

    /// <summary>Evaluates every project file git knows about (tracked or untracked, not ignored).</summary>
    public static async Task<RepoGraph> EvaluateAsync(RepoRoot root, CancellationToken cancellationToken)
    {
        // -z gives one NUL-separated path per project file, exactly as it is on disk; without it git C-quotes a path that
        // holds a quote, a backslash or a control character, and the two are not the same string.
        var listing = await ProcessRunner.RunAsync(
            "git",
            ["-c", "core.quotepath=off", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", "*.csproj"],
            root.Path,
            cancellationToken).ConfigureAwait(false);
        if (listing.ExitCode != 0)
            throw new FuseException(ErrorCode.LoadFailed, $"git cannot list the repository's files ({listing.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()}); see `git status`");
        var paths = listing.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(root.PathOf)
            .Where(p => File.Exists(p.Absolute))
            .Distinct()
            .ToList();

        MsBuildSetup.EnsureRegistered();
        return Evaluate(root, paths, cancellationToken);
    }

    private static RepoGraph Evaluate(RepoRoot root, List<RepoPath> paths, CancellationToken cancellationToken)
    {
        // Evaluation is CPU-bound and independent per project. A ProjectCollection is not thread-safe, so each worker
        // evaluates with its own; results keep the input order so the graph is deterministic.
        var nodes = new ProjectNode?[paths.Count];
        var failures = new string?[paths.Count];
        var collections = new System.Collections.Concurrent.ConcurrentBag<ProjectCollection>();
        using var local = new ThreadLocal<ProjectCollection>(() =>
        {
            var collection = new ProjectCollection();
            collections.Add(collection);
            return collection;
        });
        try
        {
            Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellationToken }, i =>
            {
                try
                {
                    nodes[i] = EvaluateOne(root, local.Value!, paths[i]);
                }
                catch (InvalidProjectFileException e)
                {
                    failures[i] = $"{paths[i].Relative}: {e.BaseMessage}";
                }
            });
        }
        finally
        {
            foreach (var collection in collections)
                collection.Dispose();
        }

        return new RepoGraph([.. nodes.OfType<ProjectNode>()], [.. failures.OfType<string>()]);
    }

    private static ProjectNode EvaluateOne(RepoRoot root, ProjectCollection collection, RepoPath path)
    {
        var outer = collection.LoadProject(path.Absolute);
        var evaluations = new List<Project> { outer };
        // A multi-targeted project's outer evaluation defines no Compile items and may condition references on the
        // target framework, so each framework's inner evaluation is read too and the results are unioned.
        if (string.IsNullOrEmpty(outer.GetPropertyValue("TargetFramework")))
        {
            foreach (var framework in outer.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                evaluations.Add(collection.LoadProject(path.Absolute, new Dictionary<string, string> { ["TargetFramework"] = framework }, toolsVersion: null));
        }

        try
        {
            return ToNode(root, evaluations);
        }
        finally
        {
            foreach (var evaluation in evaluations)
                collection.UnloadProject(evaluation);
        }
    }

    private static ProjectNode ToNode(RepoRoot root, List<Project> evaluations)
    {
        var project = evaluations[0];
        var dir = project.DirectoryPath;
        RepoPath Full(string include) => root.PathOf(System.IO.Path.Combine(dir, include));

        var path = root.PathOf(project.FullPath);
        var sources = new HashSet<RepoPath>();
        var references = new HashSet<RepoPath>();
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputs = new HashSet<RepoPath> { path };
        var isTestProperty = false;
        var isTestingPlatformApplication = false;
        foreach (var evaluation in evaluations)
        {
            foreach (var item in evaluation.GetItems("Compile"))
                sources.Add(Full(item.EvaluatedInclude));
            foreach (var itemType in new[] { "Content", "None", "RazorComponent", "AdditionalFiles" })
            {
                foreach (var item in evaluation.GetItems(itemType))
                {
                    if (PathRules.IsSource(item.EvaluatedInclude))
                        sources.Add(Full(item.EvaluatedInclude));
                }
            }

            references.UnionWith(evaluation.GetItems("ProjectReference").Select(i => Full(i.EvaluatedInclude)));
            packages.UnionWith(evaluation.GetItems("PackageReference").Select(i => i.EvaluatedInclude));
            isTestProperty |= evaluation.GetPropertyValue("IsTestProject").Equals("true", StringComparison.OrdinalIgnoreCase);
            isTestingPlatformApplication |= evaluation.GetPropertyValue("IsTestingPlatformApplication").Equals("true", StringComparison.OrdinalIgnoreCase)
                                            && !evaluation.GetPropertyValue("UseMicrosoftTestingPlatformRunner").Equals("false", StringComparison.OrdinalIgnoreCase);
            foreach (var import in evaluation.Imports)
            {
                // Most imports are the SDK's, outside the repository; testing the spelling first keeps them from
                // costing a file system call each.
                var importPath = import.ImportedProject.FullPath;
                if (root.Contains(importPath)
                    && !importPath.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    inputs.Add(root.PathOf(importPath));
            }
        }

        var isTest = isTestProperty || packages.Any(p => TestFrameworkPackages.Contains(p, StringComparer.OrdinalIgnoreCase));
        var isTestingPlatform = isTestingPlatformApplication && GlobalJson.UsesTestingPlatform(root.Path, root.Path);
        var outputType = evaluations.Select(e => e.GetPropertyValue("OutputType")).FirstOrDefault(o => o.Length > 0) ?? "";
        var isWeb = evaluations.Any(e => e.GetPropertyValue("UsingMicrosoftNETSdkWeb").Equals("true", StringComparison.OrdinalIgnoreCase));

        var assets = project.GetPropertyValue("ProjectAssetsFile");
        var runSettings = evaluations.Select(e => e.GetPropertyValue("RunSettingsFilePath").Trim()).FirstOrDefault(s => s.Length > 0);
        return new ProjectNode
        {
            Path = path,
            Name = System.IO.Path.GetFileNameWithoutExtension(project.FullPath),
            Directory = root.PathOf(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
            References = [.. references],
            Sources = sources,
            EvaluationInputs = inputs,
            AssetsFile = Full(string.IsNullOrEmpty(assets) ? System.IO.Path.Combine("obj", "project.assets.json") : assets),
            IsTest = isTest,
            UsesTestingPlatform = isTestingPlatform,
            RunSettings = runSettings is null ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, runSettings)),
            IsExecutable = !isTest && (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) || outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase) || isWeb),
        };
    }

}
