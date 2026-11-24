using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Fuse.Dotnet;
using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;

namespace Fuse.Workspace;

/// <summary>
///     Extracts <see cref="CompilationBlueprint"/> snapshots using MSBuild and immediately disposes
///     <see cref="MSBuildWorkspace"/> so the resident engine holds no MSBuild evaluation state. Supports an ephemeral
///     <c>fuse evaluate-blueprint</c> child process (<see cref="EvaluateCliAsync(string, string, CancellationToken)"/>)
///     that exits and returns 100 percent of MSBuild memory to the OS, with automatic in-process ephemeral fallback
///     when running inside unit tests or when the child command is unavailable.
/// </summary>
internal static class BlueprintEvaluator
{
    private const int MaxCliProjectArguments = 20;
    private static volatile bool _childCliUnsupported;

    /// <summary>
    ///     CLI entrypoint accepting raw <c>fuse evaluate-blueprint &lt;repoRoot&gt; &lt;outputPath&gt; [projectPaths...]</c>
    ///     arguments.
    /// </summary>
    public static Task<int> EvaluateCliAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length < 2)
            return Task.FromResult(2);
        IReadOnlyList<string> projects = args.Length > 2 ? [.. args.Skip(2)] : [];
        return EvaluateCliAsync(args[0], args[1], projects, cancellationToken);
    }

    /// <summary>
    ///     CLI entrypoint for <c>fuse evaluate-blueprint &lt;repoRoot&gt; &lt;outputPath&gt;</c>. Evaluates every
    ///     project in <paramref name="repoRoot"/> using an ephemeral <see cref="MSBuildWorkspace"/>, writes the
    ///     blueprint JSON to <paramref name="outputPath"/>, and returns an exit code (0 on success).
    /// </summary>
    public static Task<int> EvaluateCliAsync(string repoRoot, string outputPath, CancellationToken cancellationToken = default) =>
        EvaluateCliAsync(repoRoot, outputPath, [], cancellationToken);

    /// <summary>
    ///     CLI entrypoint evaluating <paramref name="projectPaths"/> (or every project when empty) in
    ///     <paramref name="repoRoot"/> and writing the resulting <see cref="CompilationBlueprint"/> to
    ///     <paramref name="outputPath"/>.
    /// </summary>
    public static async Task<int> EvaluateCliAsync(string repoRoot, string outputPath, IReadOnlyList<string> projectPaths, CancellationToken cancellationToken = default)
    {
        var root = RepoRoot.Find(repoRoot);
        if (root is null)
            return 2;
        var graph = await RepoGraph.EvaluateAsync(root, cancellationToken).ConfigureAwait(false);
        if (graph.Projects.Count == 0)
            return 1;

        var selected = SelectProjects(root, graph, projectPaths);
        if (selected.Count == 0)
            return 1;

        var outcome = await ExtractInProcessAsync(root, graph, selected, existing: null, cancellationToken).ConfigureAwait(false);
        if (outcome.Blueprint is null)
            return 1;

        var fullOutput = Path.GetFullPath(outputPath);
        if (!outcome.Blueprint.TryWrite(fullOutput))
            return 1;

        if (outcome.Failures.Count > 0)
            return 1;

        var sharedFile = CompilationBlueprint.FileFor(root.SharedStateDirectory, outcome.Blueprint.Key);
        if (!string.Equals(fullOutput, sharedFile, StringComparison.OrdinalIgnoreCase))
            outcome.Blueprint.TryWrite(sharedFile);

        return 0;
    }

    /// <summary>
    ///     Returns a valid <see cref="CompilationBlueprint"/> covering <paramref name="projects"/> and their closures
    ///     from the shared cache when available, or extracts missing projects ephemerally and updates the cache.
    /// </summary>
    public static async Task<ExtractionOutcome> EnsureProjectsAsync(
        RepoRoot root,
        RepoGraph graph,
        IReadOnlyCollection<ProjectNode> projects,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var closure = ExpandClosure(graph, projects);
        var inputs = CompilationBlueprint.ComputeInputs(root, graph);
        CompilationBlueprint? cached = null;
        if (inputs is var (key, inputHashes))
        {
            var candidate = CompilationBlueprint.TryReadFile(CompilationBlueprint.FileFor(root.SharedStateDirectory, key));
            if (candidate is not null && candidate.MatchesInputs(key, inputHashes))
            {
                cached = candidate;
                if (cached.Covers(root, closure, allowZeroRestore: true))
                    return new ExtractionOutcome(cached, new Dictionary<RepoPath, string>());
            }
        }

        // Only evaluate projects whose blueprint entry is missing or whose local assets changed.
        var toExtract = MissingOrStaleProjects(root, cached, closure);
        var unrestored = toExtract.Where(p => !File.Exists(p.AssetsFile.Absolute)).Select(p => p.Path.Relative).Distinct(StringComparer.Ordinal).ToList();
        if (unrestored.Count > 0)
            throw RestoreNeeded(unrestored);

        var started = Environment.TickCount64;
        if (ShouldUseChildProcess() && inputs is not null)
        {
            var childBlueprint = await TryExtractViaChildProcessAsync(root, toExtract, cancellationToken).ConfigureAwait(false);
            if (childBlueprint is not null)
            {
                var merged = MergeIntoSharedCache(root, inputs.Value.Key, inputs.Value.InputHashes, cached, childBlueprint);
                if (merged.Covers(root, closure, allowZeroRestore: false))
                {
                    log($"blueprint evaluated {toExtract.Count} project(s) via ephemeral child in {Environment.TickCount64 - started} ms");
                    return new ExtractionOutcome(merged, new Dictionary<RepoPath, string>());
                }
            }
        }

        var outcome = await ExtractInProcessAsync(root, graph, toExtract, cached, cancellationToken).ConfigureAwait(false);
        if (outcome.Blueprint is not null && outcome.Failures.Count == 0 && inputs is not null)
            outcome.Blueprint.TryWrite(CompilationBlueprint.FileFor(root.SharedStateDirectory, outcome.Blueprint.Key));
        log($"blueprint evaluated {toExtract.Count} project(s) in-process in {Environment.TickCount64 - started} ms ({outcome.Failures.Count} failed)");
        return outcome;
    }

    /// <summary>
    ///     Returns a restored worktree's <see cref="CompilationBlueprint"/> when <paramref name="root"/> is an
    ///     unrestored secondary worktree with identical project/props/targets/lock inputs covering
    ///     <paramref name="projects"/>, or null otherwise.
    /// </summary>
    public static CompilationBlueprint? TryFindReusableBlueprint(RepoRoot root, RepoGraph graph, IReadOnlyCollection<ProjectNode> projects)
    {
        var blueprint = CompilationBlueprint.TryLoad(root, graph);
        if (blueprint is null)
            return null;
        var closure = ExpandClosure(graph, projects);
        return blueprint.Covers(root, closure, allowZeroRestore: true) ? blueprint : null;
    }

    /// <summary>
    ///     Extracts compiler inputs using a short-lived <see cref="MSBuildWorkspace"/> and immediately disposes it
    ///     before returning. Never retained in memory.
    /// </summary>
    internal static Task<ExtractionOutcome> ExtractInProcessAsync(
        RepoRoot root,
        RepoGraph graph,
        IReadOnlyList<ProjectNode> projects,
        CompilationBlueprint? existing,
        CancellationToken cancellationToken)
    {
        EnsureMsBuildRegistered();
        return OpenAndExtractAsync(root, graph, projects, existing, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureMsBuildRegistered() => MsBuildSetup.EnsureRegistered();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ExtractionOutcome> OpenAndExtractAsync(
        RepoRoot root,
        RepoGraph graph,
        IReadOnlyList<ProjectNode> projects,
        CompilationBlueprint? existing,
        CancellationToken cancellationToken)
    {
        var failures = new ConcurrentDictionary<RepoPath, string>();
        RepoPath? currentProject = null;

        Solution? extractedSolution;
        using (var workspace = MSBuildWorkspace.Create())
        {
            workspace.LoadMetadataForReferencedProjects = false;
            workspace.SkipUnrecognizedProjects = true;
            workspace.RegisterWorkspaceFailedHandler(e =>
            {
                if (e.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
                    return;
                if (LoadFailure.IsWarning(e.Diagnostic.Message))
                    return;
                var matched = graph.Projects.FirstOrDefault(p => e.Diagnostic.Message.Contains(p.Path.Absolute, StringComparison.OrdinalIgnoreCase))?.Path
                    ?? currentProject;
                if (matched is { } failed)
                    failures.TryAdd(failed, e.Diagnostic.Message);
            });

            foreach (var project in projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (workspace.CurrentSolution.Projects.Any(p => project.Path.Matches(p.FilePath)))
                    continue;
                currentProject = project.Path;
                try
                {
                    await workspace.OpenProjectAsync(project.Path.Absolute, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException)
                {
                    failures.TryAdd(project.Path, e.Message);
                }
            }

            extractedSolution = workspace.CurrentSolution;
        }

        var failureMap = new Dictionary<RepoPath, string>(failures);
        var blueprintProjects = new List<CompilationBlueprint.BlueprintProject>();
        var assetsHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var roslynProject in extractedSolution.Projects)
        {
            if (roslynProject.FilePath is null)
                continue;
            var node = graph.Find(root.PathOf(roslynProject.FilePath));
            var bp = await CaptureProjectAsync(root, extractedSolution, roslynProject, node, cancellationToken).ConfigureAwait(false);
            if (bp is not null)
                blueprintProjects.Add(bp);
            if (node is not null && File.Exists(node.AssetsFile.Absolute) && CompilationBlueprint.HashAssets(root, node.AssetsFile.Absolute) is { } hash)
                assetsHashes[node.AssetsFile.Relative] = hash;
        }

        var computed = CompilationBlueprint.ComputeInputs(root, graph);
        var key = computed?.Key ?? existing?.Key ?? Guid.NewGuid().ToString("N");
        var inputHashes = computed?.InputHashes ?? existing?.InputHashes ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var blueprint = existing is not null && string.Equals(existing.Key, key, StringComparison.Ordinal)
            ? existing.Merge(root.Path, inputHashes, assetsHashes, blueprintProjects)
            : new CompilationBlueprint(CompilationBlueprint.FormatVersion, CompilationBlueprint.CurrentBuild, key, root.Path, inputHashes, assetsHashes, blueprintProjects);

        return new ExtractionOutcome(blueprint, failureMap);
    }

    private static async Task<CompilationBlueprint.BlueprintProject?> CaptureProjectAsync(
        RepoRoot root,
        Solution solution,
        Project project,
        ProjectNode? node,
        CancellationToken cancellationToken)
    {
        if (project.FilePath is null)
            return null;
        var projectDir = Path.GetDirectoryName(project.FilePath) ?? root.Path;

        var compile = new List<CompilationBlueprint.BlueprintDocument>();
        foreach (var doc in project.Documents)
        {
            if (string.IsNullOrEmpty(doc.FilePath))
                continue;
            string? inline = null;
            if (PathRules.IsBuildOutput(projectDir, doc.FilePath))
            {
                var text = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                inline = CompilationBlueprint.MaskRootText(root, text.ToString());
            }

            compile.Add(new CompilationBlueprint.BlueprintDocument(
                CompilationBlueprint.StorePath(root, doc.FilePath),
                [.. doc.Folders],
                inline));
        }

        var additional = new List<CompilationBlueprint.BlueprintDocument>();
        foreach (var doc in project.AdditionalDocuments)
        {
            if (string.IsNullOrEmpty(doc.FilePath))
                continue;
            string? inline = null;
            if (PathRules.IsBuildOutput(projectDir, doc.FilePath))
            {
                var text = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                inline = CompilationBlueprint.MaskRootText(root, text.ToString());
            }

            additional.Add(new CompilationBlueprint.BlueprintDocument(
                CompilationBlueprint.StorePath(root, doc.FilePath),
                [.. doc.Folders],
                inline));
        }

        var analyzerConfigs = new List<CompilationBlueprint.BlueprintDocument>();
        foreach (var doc in project.AnalyzerConfigDocuments)
        {
            if (string.IsNullOrEmpty(doc.FilePath))
                continue;
            string? inline = null;
            if (PathRules.IsBuildOutput(projectDir, doc.FilePath))
            {
                var text = await doc.GetTextAsync(cancellationToken).ConfigureAwait(false);
                inline = CompilationBlueprint.MaskRootText(root, text.ToString());
            }

            analyzerConfigs.Add(new CompilationBlueprint.BlueprintDocument(
                CompilationBlueprint.StorePath(root, doc.FilePath),
                [.. doc.Folders],
                inline));
        }

        var referencePaths = new List<string>();
        foreach (var reference in project.MetadataReferences)
        {
            if (reference is PortableExecutableReference pe && !string.IsNullOrEmpty(pe.FilePath))
                referencePaths.Add(CompilationBlueprint.StorePath(root, pe.FilePath));
        }

        var analyzers = new List<string>();
        foreach (var reference in project.AnalyzerReferences)
        {
            if (reference is AnalyzerFileReference file && !string.IsNullOrEmpty(file.FullPath))
                analyzers.Add(CompilationBlueprint.StorePath(root, file.FullPath));
        }

        var projectRefs = new List<CompilationBlueprint.BlueprintProjectReference>();
        foreach (var pr in project.ProjectReferences)
        {
            if (solution.GetProject(pr.ProjectId) is { FilePath: not null } target)
            {
                projectRefs.Add(new CompilationBlueprint.BlueprintProjectReference(
                    CompilationBlueprint.StorePath(root, target.FilePath),
                    target.Name,
                    [.. pr.Aliases],
                    pr.EmbedInteropTypes));
            }
        }

        var csharpOptions = project.CompilationOptions as CSharpCompilationOptions;
        var csharpParse = project.ParseOptions as CSharpParseOptions;
        var specific = csharpOptions is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : csharpOptions.SpecificDiagnosticOptions.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
        var features = csharpParse is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : csharpParse.Features.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        string? tfm = null;
        if (project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.TargetFramework", out var buildTfm) && !string.IsNullOrWhiteSpace(buildTfm))
            tfm = buildTfm;
        else
            tfm = InferTargetFrameworkFromName(project.Name);

        return new CompilationBlueprint.BlueprintProject(
            ProjectPath: CompilationBlueprint.StorePath(root, project.FilePath),
            Name: project.Name,
            AssemblyName: project.AssemblyName,
            Compile: compile,
            ReferencePath: referencePaths,
            Analyzer: analyzers,
            AdditionalFiles: additional,
            AnalyzerConfigFiles: analyzerConfigs,
            DefineConstants: csharpParse is null ? [] : [.. csharpParse.PreprocessorSymbolNames],
            LanguageVersion: (csharpParse?.LanguageVersion ?? LanguageVersion.Default).ToString(),
            OutputKind: (csharpOptions?.OutputKind ?? OutputKind.DynamicallyLinkedLibrary).ToString(),
            Nullable: (csharpOptions?.NullableContextOptions ?? NullableContextOptions.Disable).ToString(),
            AllowUnsafe: csharpOptions?.AllowUnsafe ?? false,
            SpecificDiagnosticOptions: specific,
            OutputPath: string.IsNullOrEmpty(project.OutputFilePath) ? null : CompilationBlueprint.StorePath(root, project.OutputFilePath),
            ProjectReferences: projectRefs,
            TargetFramework: tfm,
            IsTestProject: node?.IsTest ?? false,
            TestFramework: node?.Framework ?? TestFramework.Unknown,
            OutputRefPath: string.IsNullOrEmpty(project.OutputRefFilePath) ? null : CompilationBlueprint.StorePath(root, project.OutputRefFilePath),
            GeneralDiagnosticOption: (csharpOptions?.GeneralDiagnosticOption ?? ReportDiagnostic.Default).ToString(),
            WarningLevel: csharpOptions?.WarningLevel ?? 4,
            CheckOverflow: csharpOptions?.CheckOverflow ?? false,
            Deterministic: csharpOptions?.Deterministic ?? true,
            Platform: (csharpOptions?.Platform ?? Platform.AnyCpu).ToString(),
            OptimizationLevel: (csharpOptions?.OptimizationLevel ?? OptimizationLevel.Debug).ToString(),
            MetadataImportOptions: (csharpOptions?.MetadataImportOptions ?? MetadataImportOptions.Public).ToString(),
            DocumentationMode: (csharpParse?.DocumentationMode ?? DocumentationMode.Parse).ToString(),
            Features: features,
            Usings: csharpOptions is null ? [] : [.. csharpOptions.Usings]);
    }

    private static string? InferTargetFrameworkFromName(string projectName)
    {
        var open = projectName.LastIndexOf('(');
        var close = projectName.LastIndexOf(')');
        if (open > 0 && close > open + 1 && close == projectName.Length - 1)
            return projectName[(open + 1)..close].Trim();
        return null;
    }

    private static List<ProjectNode> ExpandClosure(RepoGraph graph, IEnumerable<ProjectNode> projects)
    {
        var seen = new HashSet<RepoPath>();
        var list = new List<ProjectNode>();
        foreach (var root in projects)
        {
            foreach (var node in graph.ClosureOf(root))
            {
                if (seen.Add(node.Path))
                    list.Add(node);
            }
        }

        return list;
    }

    private static List<ProjectNode> MissingOrStaleProjects(RepoRoot root, CompilationBlueprint? cached, List<ProjectNode> closure)
    {
        if (cached is null)
            return closure;
        var covered = new HashSet<string>(cached.Projects.Select(p => p.ProjectPath), StringComparer.Ordinal);
        var stale = new List<ProjectNode>();
        foreach (var node in closure)
        {
            if (!covered.Contains(node.Path.Relative))
            {
                stale.Add(node);
                continue;
            }

            if (File.Exists(node.AssetsFile.Absolute))
            {
                var hash = CompilationBlueprint.HashAssets(root, node.AssetsFile.Absolute);
                if (!cached.AssetsHashes.TryGetValue(node.AssetsFile.Relative, out var expected) || !string.Equals(hash, expected, StringComparison.Ordinal))
                    stale.Add(node);
            }
        }

        return ExpandClosureFromList(stale, closure);
    }

    private static List<ProjectNode> ExpandClosureFromList(List<ProjectNode> subset, List<ProjectNode> all)
    {
        if (subset.Count == 0)
            return [];
        var wanted = new HashSet<RepoPath>(subset.Select(p => p.Path));
        return [.. all.Where(p => wanted.Contains(p.Path))];
    }

    private static List<ProjectNode> SelectProjects(RepoRoot root, RepoGraph graph, IReadOnlyList<string> projectPaths)
    {
        if (projectPaths.Count == 0)
            return [.. graph.Projects];
        var selected = new List<ProjectNode>();
        foreach (var spec in projectPaths)
        {
            if (graph.Find(root.PathOf(spec)) is { } node)
                selected.Add(node);
        }

        return ExpandClosure(graph, selected);
    }

    private static bool ShouldUseChildProcess()
    {
        if (_childCliUnsupported)
            return false;
        if (string.Equals(Environment.GetEnvironmentVariable("FUSE_BLUEPRINT_INPROC"), "1", StringComparison.Ordinal))
            return false;
        if (!string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, "fuse", StringComparison.OrdinalIgnoreCase))
            return false;
        return FindFuseAssembly() is not null;
    }

    private static string? FindFuseAssembly()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "fuse.dll");
        return File.Exists(dll) ? dll : null;
    }

    private static async Task<CompilationBlueprint?> TryExtractViaChildProcessAsync(RepoRoot root, List<ProjectNode> projects, CancellationToken cancellationToken)
    {
        var assembly = FindFuseAssembly();
        var host = Environment.ProcessPath;
        if (assembly is null || string.IsNullOrEmpty(host))
            return null;

        var tempFile = Path.Combine(Path.GetTempPath(), $"fuse-bp-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        try
        {
            var args = new List<string>();
            if (!Path.GetFileNameWithoutExtension(host).Equals("fuse", StringComparison.OrdinalIgnoreCase))
                args.Add(assembly);
            args.Add("evaluate-blueprint");
            args.Add(root.Path);
            args.Add(tempFile);
            if (projects.Count <= MaxCliProjectArguments)
            {
                foreach (var p in projects)
                    args.Add(p.Path.Relative);
            }

            var result = await ProcessRunner.RunAsync(host, args, root.Path, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 2 && result.Output.Contains("usage:", StringComparison.OrdinalIgnoreCase))
            {
                _childCliUnsupported = true;
                return null;
            }

            if (result.ExitCode != 0)
                return null;
            return CompilationBlueprint.TryReadFile(tempFile);
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static CompilationBlueprint MergeIntoSharedCache(
        RepoRoot root,
        string key,
        Dictionary<string, string> inputHashes,
        CompilationBlueprint? cached,
        CompilationBlueprint incoming)
    {
        var merged = cached is not null && string.Equals(cached.Key, key, StringComparison.Ordinal)
            ? cached.Merge(incoming.SourceRoot, inputHashes, incoming.AssetsHashes, incoming.Projects)
            : incoming;
        merged.TryWrite(CompilationBlueprint.FileFor(root.SharedStateDirectory, key));
        return merged;
    }

    private static FuseException RestoreNeeded(List<string> unrestored) =>
        new(
            ErrorCode.RestoreNeeded,
            $"restore needed: run `dotnet restore {unrestored[0]}`{(unrestored.Count > 1 ? " and the same for each project listed" : "")} ({string.Join(", ", unrestored.Take(3))}{(unrestored.Count > 3 ? ", ..." : "")} not restored)");

    /// <summary>Result of extracting a blueprint: the blueprint (when any project loaded) and per-project MSBuild load failures.</summary>
    internal sealed record ExtractionOutcome(CompilationBlueprint? Blueprint, IReadOnlyDictionary<RepoPath, string> Failures);
}
