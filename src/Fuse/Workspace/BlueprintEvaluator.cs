using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Fuse.Failures;
using Fuse.Graph;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild;

namespace Fuse.Workspace;

/// <summary>
///     Extracts <see cref="CompilationBlueprint"/> snapshots in the engine process with a short-lived
///     <see cref="MSBuildWorkspace"/>, disposed before the blueprint is captured from its solution snapshot, so only
///     MSBuild's static caches and loaded assemblies stay resident.
/// </summary>
internal static class BlueprintEvaluator
{
    /// <summary>
    ///     One extraction lock per shared state directory. Concurrent extractions for the same cache would run
    ///     duplicate MSBuild sessions over the same projects and contend on MSBuild's in-process state, so each
    ///     extraction waits here and re-reads the cache after it acquires the lock, extracting only what is still
    ///     missing instead of repeating the extraction it waited for. One entry per directory seen, never removed.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _extractionLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Test seam: when set, called with each project's absolute path before it opens during an extraction. A
    ///     non-null return is thrown and classified exactly as if <c>OpenProjectAsync</c> had thrown it, so tests
    ///     fail one extraction transiently without waiting for a real BuildHost timeout. Tests key it on their own
    ///     repository's paths and reset it afterwards, because other tests extract in the same process.
    /// </summary>
    internal static Func<string, Exception?>? TestOpenFault { get; set; }

    // Global properties for the ephemeral extraction workspace. MSBuildWorkspace runs a CoreCompile execution
    // pass for the compiler command-line arguments, and MsCoverageReferencedPathMaps (Microsoft.CodeCoverage)
    // writes .msCoverageSourceRootsMapping_* into bin/ from it unless DisableMsCoverageReferencedPathMaps is set.
    private static readonly Dictionary<string, string> DesignTimeProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DesignTimeBuild"] = "true",
        ["BuildingProject"] = "false",
        ["SkipCompilerExecution"] = "true",
        ["ProvideCommandLineArgs"] = "true",
        ["DisableMsCoverageReferencedPathMaps"] = "true",
    };

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
        if (TryReadValid(root, inputs) is { } hit && hit.Covers(root, closure, allowZeroRestore: true))
            return new ExtractionOutcome(hit, new Dictionary<RepoPath, string>());

        var extraction = _extractionLocks.GetOrAdd(root.SharedStateDirectory, _ => new SemaphoreSlim(1, 1));
        await extraction.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // An extraction that ran while this call waited may have covered the closure already.
            var cached = TryReadValid(root, inputs);
            if (cached is not null && cached.Covers(root, closure, allowZeroRestore: true))
                return new ExtractionOutcome(cached, new Dictionary<RepoPath, string>());

            // Only evaluate projects whose blueprint entry is missing or whose local assets changed.
            var toExtract = MissingOrStaleProjects(root, cached, closure);
            var unrestored = toExtract.Where(p => !File.Exists(p.AssetsFile.Absolute)).Select(p => p.Path.Relative).Distinct(StringComparer.Ordinal).ToList();
            if (unrestored.Count > 0)
                throw RestoreNeeded(unrestored);

            var started = Environment.TickCount64;
            var outcome = await ExtractAsync(root, graph, toExtract, cached, cancellationToken).ConfigureAwait(false);
            outcome = await RetryTransientAsync(root, graph, toExtract, outcome, log, cancellationToken).ConfigureAwait(false);
            if (outcome.Blueprint is not null && outcome.Failures.Count == 0 && outcome.TransientFailure is null && inputs is not null)
                outcome.Blueprint.TryWrite(CompilationBlueprint.FileFor(root.SharedStateDirectory, outcome.Blueprint.Key));
            log($"blueprint evaluated {toExtract.Count} project(s) in {Environment.TickCount64 - started} ms ({outcome.Failures.Count} failed)");
            return outcome;
        }
        finally
        {
            extraction.Release();
        }
    }

    /// <summary>
    ///     Extracts again the projects a transient host failure kept out of <paramref name="outcome"/>, once, with a
    ///     fresh workspace and so a fresh BuildHost. A transient report with every project captured anyway clears
    ///     without extracting again. The retry merges into the first attempt's blueprint; a transient failure the
    ///     retry also hits stays on the outcome for the caller to answer without recording.
    /// </summary>
    private static async Task<ExtractionOutcome> RetryTransientAsync(
        RepoRoot root,
        RepoGraph graph,
        IReadOnlyList<ProjectNode> attempted,
        ExtractionOutcome outcome,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        if (outcome.TransientFailure is not { } transient)
            return outcome;
        var captured = outcome.Blueprint?.Projects.Select(p => p.ProjectPath).ToHashSet(StringComparer.Ordinal) ?? [];
        var retry = attempted.Where(p => !captured.Contains(p.Path.Relative) && !outcome.Failures.ContainsKey(p.Path)).ToList();
        if (retry.Count == 0)
            return outcome with { TransientFailure = null };
        cancellationToken.ThrowIfCancellationRequested();
        log($"blueprint extraction hit a transient failure, retrying {retry.Count} project(s): {FirstLine(transient)}");
        var second = await ExtractAsync(root, graph, retry, outcome.Blueprint, cancellationToken).ConfigureAwait(false);
        var failures = new Dictionary<RepoPath, string>(outcome.Failures);
        foreach (var (failedPath, reason) in second.Failures)
            failures[failedPath] = reason;
        return new ExtractionOutcome(second.Blueprint ?? outcome.Blueprint, failures, second.TransientFailure);
    }

    /// <summary>Returns the shared cache's blueprint when its key and input hashes match <paramref name="inputs"/>, or null.</summary>
    private static CompilationBlueprint? TryReadValid(
        RepoRoot root,
        (string Key, Dictionary<string, string> InputHashes)? inputs)
    {
        if (inputs is not var (key, inputHashes))
            return null;
        var candidate = CompilationBlueprint.TryReadFile(CompilationBlueprint.FileFor(root.SharedStateDirectory, key));
        return candidate is not null && candidate.MatchesInputs(key, inputHashes) ? candidate : null;
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
    internal static Task<ExtractionOutcome> ExtractAsync(
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
        string? transientFailure = null;
        RepoPath? currentProject = null;

        Solution? extractedSolution;
        using (var workspace = MSBuildWorkspace.Create(DesignTimeProperties))
        {
            workspace.LoadMetadataForReferencedProjects = false;
            workspace.SkipUnrecognizedProjects = true;
            workspace.RegisterWorkspaceFailedHandler(e =>
            {
                if (e.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
                    return;
                if (LoadFailure.IsWarning(e.Diagnostic.Message))
                    return;
                if (IsTransient(e.Diagnostic.Message))
                {
                    Interlocked.CompareExchange(ref transientFailure, e.Diagnostic.Message, null);
                    return;
                }

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
                    if (TestOpenFault?.Invoke(project.Path.Absolute) is { } fault)
                        throw fault;
                    await workspace.OpenProjectAsync(project.Path.Absolute, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException or TimeoutException)
                {
                    if (e is TimeoutException || e is IOException || IsTransient(e.Message))
                        Interlocked.CompareExchange(ref transientFailure, e.Message, null);
                    else
                        failures.TryAdd(project.Path, e.Message);
                }
            }

            extractedSolution = workspace.CurrentSolution;
        }

        var failureMap = new Dictionary<RepoPath, string>(failures);
        var blueprintProjects = new List<CompilationBlueprint.BlueprintProject>();
        var assetsHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var knownSources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var roslynProject in extractedSolution.Projects)
        {
            if (roslynProject.FilePath is null)
                continue;
            var node = graph.Find(root.PathOf(roslynProject.FilePath));
            var bp = await CaptureProjectAsync(root, extractedSolution, roslynProject, node, cancellationToken).ConfigureAwait(false);
            if (bp is not null)
                blueprintProjects.Add(bp);
            if (node is null)
                continue;
            if (File.Exists(node.AssetsFile.Absolute) && CompilationBlueprint.HashAssets(root, node.AssetsFile.Absolute) is { } hash)
                assetsHashes[node.AssetsFile.Relative] = hash;
            knownSources[node.Path.Relative] = [.. node.Sources.Select(s => s.Relative).Order(StringComparer.Ordinal)];
        }

        var computed = CompilationBlueprint.ComputeInputs(root, graph);
        var key = computed?.Key ?? existing?.Key ?? Guid.NewGuid().ToString("N");
        var inputHashes = computed?.InputHashes ?? existing?.InputHashes ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var blueprint = existing is not null && string.Equals(existing.Key, key, StringComparison.Ordinal)
            ? existing.Merge(root.Path, inputHashes, assetsHashes, blueprintProjects, knownSources)
            : new CompilationBlueprint(CompilationBlueprint.FormatVersion, CompilationBlueprint.CurrentBuild, key, root.Path, inputHashes, assetsHashes, blueprintProjects, knownSources);

        return new ExtractionOutcome(blueprint, failureMap, transientFailure);
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

        string? cryptoKeyFile = null;
        if (!string.IsNullOrEmpty(csharpOptions?.CryptoKeyFile))
        {
            // MSBuild spells the key against the project directory without normalizing (eng\..\key.snk), so it is
            // resolved first; the stored spelling then matches what the load resolves it to.
            cryptoKeyFile = CompilationBlueprint.StorePath(root, Path.GetFullPath(Path.Combine(projectDir, csharpOptions.CryptoKeyFile)));
        }

        string? tfm = null;
        if (project.AnalyzerOptions.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.TargetFramework", out var buildTfm) && !string.IsNullOrWhiteSpace(buildTfm))
            tfm = buildTfm;
        else
            tfm = InferTargetFrameworkFromName(project.Name);

        string? cryptoPublicKey = null;
        if (csharpOptions is not null && !csharpOptions.CryptoPublicKey.IsDefault)
            cryptoPublicKey = Convert.ToHexString(csharpOptions.CryptoPublicKey.AsSpan());

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
            Usings: csharpOptions is null ? [] : [.. csharpOptions.Usings],
            ModuleName: csharpOptions?.ModuleName,
            MainTypeName: csharpOptions?.MainTypeName,
            ScriptClassName: csharpOptions?.ScriptClassName,
            CryptoKeyFile: cryptoKeyFile,
            CryptoKeyContainer: csharpOptions?.CryptoKeyContainer,
            DelaySign: csharpOptions?.DelaySign,
            CryptoPublicKey: cryptoPublicKey,
            StrongNameProvider: csharpOptions?.StrongNameProvider?.GetType().FullName);
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

    private static FuseException RestoreNeeded(List<string> unrestored) =>
        new(
            ErrorCode.RestoreNeeded,
            $"restore needed: run `dotnet restore {unrestored[0]}`{(unrestored.Count > 1 ? " and the same for each project listed" : "")} ({string.Join(", ", unrestored.Take(3))}{(unrestored.Count > 3 ? ", ..." : "")} not restored)");

    /// <summary>
    ///     True when <paramref name="message"/> reports the extraction host rather than the project: the BuildHost
    ///     did not respond, exited, disconnected or timed out. Such a failure is never recorded as the project's
    ///     load failure, and the extraction is tried once more. Each marker needs a non-letter-or-digit on both
    ///     sides, so a project or directory named like one (a timeout policy test) does not read as a host failure.
    /// </summary>
    internal static bool IsTransient(string message) =>
        ContainsBounded(message, "buildhost pid")
        || ContainsBounded(message, "buildhost process")
        || ContainsBounded(message, "build host")
        || ContainsBounded(message, "not responding")
        || ContainsBounded(message, "timed out")
        || ContainsBounded(message, "timeout")
        || ContainsBounded(message, "disconnected")
        || ContainsBounded(message, "disconnect");

    private static bool ContainsBounded(string message, string marker)
    {
        var index = 0;
        while ((index = message.IndexOf(marker, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(message[index - 1]);
            var end = index + marker.Length;
            var after = end == message.Length || !char.IsLetterOrDigit(message[end]);
            if (before && after)
                return true;
            index++;
        }

        return false;
    }

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        return end < 0 ? message : message[..end];
    }

    /// <summary>Result of extracting a blueprint: the blueprint (when any project loaded), per-project deterministic MSBuild load failures, and the first transient host failure (when one struck), which the caller answers without recording.</summary>
    internal sealed record ExtractionOutcome(CompilationBlueprint? Blueprint, IReadOnlyDictionary<RepoPath, string> Failures, string? TransientFailure = null);
}
