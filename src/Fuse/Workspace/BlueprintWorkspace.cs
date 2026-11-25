using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Fuse.Graph;
using Fuse.Paths;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;

namespace Fuse.Workspace;

/// <summary>
///     Hosts projects loaded from a <see cref="CompilationBlueprint"/> inside a lean <see cref="AdhocWorkspace"/>
///     instead of keeping an <see cref="Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace"/> resident in memory. Supports
///     shared immutable metadata/analyzer/HEAD-text caches across worktrees and true single-project unloading via
///     <see cref="UnloadProjects"/>.
/// </summary>
internal sealed class BlueprintWorkspace : IDisposable
{
    private readonly AdhocWorkspace _workspace;
    private readonly SharedResources _shared;
    private readonly Dictionary<(RepoPath ProjectPath, string ProjectName), ProjectId> _projectIds = [];
    private readonly Dictionary<RepoPath, List<ProjectId>> _projectsByPath = [];

    public BlueprintWorkspace(SharedResources? shared = null)
    {
        _workspace = new AdhocWorkspace();
        var loader = _workspace.Services.GetRequiredService<IAnalyzerService>().GetLoader();
        _shared = shared ?? new SharedResources(loader);
    }

    /// <summary>Shared immutable resources (metadata references, analyzers, HEAD source texts) reusable across worktrees.</summary>
    public SharedResources Shared => _shared;

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
            .GroupBy(p => targetRoot.PathOf(CompilationBlueprint.ResolvePath(targetRoot, p.ProjectPath)))
            .ToDictionary(g => g.Key, g => g.ToList());

        var wanted = new List<(RepoPath Path, ProjectNode Node, CompilationBlueprint.BlueprintProject Blueprint)>();
        foreach (var node in closure)
        {
            if (!byPath.TryGetValue(node.Path, out var variants))
                continue;
            foreach (var variant in variants)
            {
                if (!_projectIds.ContainsKey((node.Path, variant.Name)))
                    wanted.Add((node.Path, node, variant));
            }
        }

        if (wanted.Count == 0)
            return;

        var ordered = OrderTopologically(targetRoot, wanted);
        var pendingIds = new Dictionary<(RepoPath Path, string Name), ProjectId>(_projectIds);
        foreach (var (path, _, bp) in ordered)
            pendingIds[(path, bp.Name)] = ProjectId.CreateNewId(debugName: bp.Name);

        var knownByPath = KnownSourcesByPath(targetRoot, byPath, blueprint);
        var infos = new List<ProjectInfo>(ordered.Count);
        var addedByPath = new Dictionary<RepoPath, List<(string Name, ProjectId Id)>>();
        foreach (var (path, node, bp) in ordered)
        {
            var id = pendingIds[(path, bp.Name)];
            var info = BuildProjectInfo(id, targetRoot, node, bp, blueprint.SourceRoot, pendingIds, head, dirtyPaths, knownByPath[path]);
            infos.Add(info);
            if (!addedByPath.TryGetValue(path, out var list))
                addedByPath[path] = list = [];
            list.Add((bp.Name, id));
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
                if (blueprint.KnownSources.TryGetValue(variant.ProjectPath, out var sources))
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
                    foreach (var doc in variant.Compile)
                        known.Add(targetRoot.PathOf(CompilationBlueprint.ResolvePath(targetRoot, doc.Path)));
                }
            }

            result[path] = known;
        }

        return result;
    }

    private ProjectInfo BuildProjectInfo(
        ProjectId id,
        RepoRoot targetRoot,
        ProjectNode node,
        CompilationBlueprint.BlueprintProject bp,
        string sourceRoot,
        Dictionary<(RepoPath Path, string Name), ProjectId> projectIds,
        string? head,
        IReadOnlySet<RepoPath>? dirtyPaths,
        HashSet<RepoPath> knownSources)
    {
        var projectFile = targetRoot.PathOf(CompilationBlueprint.ResolvePath(targetRoot, bp.ProjectPath)).Absolute;
        var projectDir = Path.GetDirectoryName(projectFile) ?? targetRoot.Path;
        var isSecondaryWorktree = !targetRoot.PathOf(sourceRoot).Equals(targetRoot.PathOf(targetRoot.Path));

        var projectReferences = new List<ProjectReference>(bp.ProjectReferences.Count);
        foreach (var pr in bp.ProjectReferences)
        {
            var resolvedTarget = CompilationBlueprint.ResolvePath(targetRoot, pr.TargetProjectPath);
            var targetPath = targetRoot.PathOf(resolvedTarget);
            if (projectIds.TryGetValue((targetPath, pr.TargetProjectName), out var targetId))
            {
                projectReferences.Add(new ProjectReference(targetId, [.. pr.Aliases], pr.EmbedInteropTypes));
            }
            else if (_projectsByPath.TryGetValue(targetPath, out var fallbackIds) && fallbackIds.Count > 0)
            {
                projectReferences.Add(new ProjectReference(fallbackIds[0], [.. pr.Aliases], pr.EmbedInteropTypes));
            }
            else if (!targetRoot.Contains(resolvedTarget))
            {
                throw new InvalidOperationException($"blueprint cannot load {bp.ProjectPath}: it references {pr.TargetProjectPath} outside the repository");
            }
        }

        var metadataReferences = new List<MetadataReference>(bp.ReferencePath.Count);
        foreach (var refPath in bp.ReferencePath)
        {
            var resolved = ResolveExistingPath(targetRoot, sourceRoot, refPath, isSecondaryWorktree);
            if (resolved is not null && _shared.GetMetadataReference(resolved) is { } metadata)
                metadataReferences.Add(metadata);
        }

        var analyzerReferences = new List<AnalyzerReference>(bp.Analyzer.Count);
        foreach (var analyzerPath in bp.Analyzer)
        {
            var resolved = ResolveExistingPath(targetRoot, sourceRoot, analyzerPath, isSecondaryWorktree);
            if (resolved is not null && _shared.GetAnalyzerReference(resolved) is { } analyzer)
                analyzerReferences.Add(analyzer);
        }

        var documents = new List<DocumentInfo>(bp.Compile.Count + node.Sources.Count);
        var seenDocPaths = new HashSet<RepoPath>();
        foreach (var doc in bp.Compile)
        {
            var resolved = CompilationBlueprint.ResolvePath(targetRoot, doc.Path);
            var repoPath = targetRoot.PathOf(resolved);
            if (!seenDocPaths.Add(repoPath))
                continue;
            var isGenerated = PathRules.IsBuildOutput(projectDir, resolved);
            var textAndVersion = LoadDocumentText(targetRoot, sourceRoot, repoPath, resolved, doc.InlineText, isGenerated, isSecondaryWorktree, head, dirtyPaths);
            if (textAndVersion is null)
                continue;
            documents.Add(DocumentInfo.Create(
                DocumentId.CreateNewId(id, resolved),
                Path.GetFileName(resolved),
                folders: doc.Folders,
                loader: TextLoader.From(textAndVersion),
                filePath: resolved));
        }

        // Include working-tree sources the graph names that the extraction never evaluated: files added after
        // the blueprint was cached. A source the extraction evaluated but this variant does not compile was left
        // out of it on purpose (a file conditioned on another target framework), so it stays out.
        foreach (var sourcePath in node.Sources)
        {
            if (knownSources.Contains(sourcePath) || !seenDocPaths.Add(sourcePath) || !File.Exists(sourcePath.Absolute))
                continue;
            var textAndVersion = LoadDocumentText(targetRoot, sourceRoot, sourcePath, sourcePath.Absolute, inlineText: null, isGenerated: false, isSecondaryWorktree, head, dirtyPaths);
            if (textAndVersion is null)
                continue;
            var folders = RelativeFolders(node.Directory.Absolute, sourcePath.Absolute);
            documents.Add(DocumentInfo.Create(
                DocumentId.CreateNewId(id, sourcePath.Absolute),
                sourcePath.FileName,
                folders: folders,
                loader: TextLoader.From(textAndVersion),
                filePath: sourcePath.Absolute));
        }

        var additional = BuildSupplementaryDocuments(id, targetRoot, sourceRoot, projectDir, bp.AdditionalFiles, isSecondaryWorktree);
        var analyzerConfigs = BuildSupplementaryDocuments(id, targetRoot, sourceRoot, projectDir, bp.AnalyzerConfigFiles, isSecondaryWorktree);

        var compilationOptions = BuildCompilationOptions(bp, targetRoot);
        var parseOptions = BuildParseOptions(bp);
        var outputPath = ResolveOutputPath(targetRoot, bp.OutputPath);
        var outputRefPath = ResolveOutputPath(targetRoot, bp.OutputRefPath);

        return ProjectInfo.Create(
                id,
                VersionStamp.Create(),
                bp.Name,
                bp.AssemblyName,
                LanguageNames.CSharp,
                filePath: projectFile,
                outputFilePath: outputPath,
                compilationOptions: compilationOptions,
                parseOptions: parseOptions,
                documents: documents,
                projectReferences: projectReferences,
                metadataReferences: metadataReferences,
                analyzerReferences: analyzerReferences,
                additionalDocuments: additional)
            .WithOutputRefFilePath(outputRefPath)
            .WithAnalyzerConfigDocuments(analyzerConfigs);
    }

    private static Solution AddProjectsToSolution(Solution solution, IEnumerable<ProjectInfo> projects)
    {
        var next = solution;
        foreach (var info in projects)
            next = next.AddProject(info);
        return next;
    }

    private static List<DocumentInfo> BuildSupplementaryDocuments(
        ProjectId projectId,
        RepoRoot targetRoot,
        string sourceRoot,
        string projectDir,
        IReadOnlyList<CompilationBlueprint.BlueprintDocument> docs,
        bool isSecondaryWorktree)
    {
        var result = new List<DocumentInfo>(docs.Count);
        foreach (var doc in docs)
        {
            var resolved = CompilationBlueprint.ResolvePath(targetRoot, doc.Path);
            var isGenerated = PathRules.IsBuildOutput(projectDir, resolved);
            var textAndVersion = LoadSupplementaryText(targetRoot, sourceRoot, doc.Path, resolved, doc.InlineText, isGenerated, isSecondaryWorktree);
            if (textAndVersion is null)
                continue;
            result.Add(DocumentInfo.Create(
                DocumentId.CreateNewId(projectId, resolved),
                Path.GetFileName(resolved),
                folders: doc.Folders,
                loader: TextLoader.From(textAndVersion),
                filePath: resolved));
        }

        return result;
    }

    private TextAndVersion? LoadDocumentText(
        RepoRoot targetRoot,
        string sourceRoot,
        RepoPath repoPath,
        string resolvedPath,
        string? inlineText,
        bool isGenerated,
        bool isSecondaryWorktree,
        string? head,
        IReadOnlySet<RepoPath>? dirtyPaths)
    {
        if (isGenerated)
            return LoadGeneratedText(targetRoot, sourceRoot, repoPath.Relative, resolvedPath, inlineText, isSecondaryWorktree);

        if (!File.Exists(resolvedPath))
            return null;

        var canShareHead = head is not null
            && (dirtyPaths is null || !dirtyPaths.Contains(repoPath))
            && targetRoot.Contains(resolvedPath);
        if (canShareHead)
            return _shared.GetOrReadHeadText(head!, repoPath.Relative, resolvedPath);

        return ReadDiskText(resolvedPath);
    }

    private static TextAndVersion? LoadSupplementaryText(
        RepoRoot targetRoot,
        string sourceRoot,
        string storedPath,
        string resolvedPath,
        string? inlineText,
        bool isGenerated,
        bool isSecondaryWorktree)
    {
        if (isGenerated)
            return LoadGeneratedText(targetRoot, sourceRoot, storedPath, resolvedPath, inlineText, isSecondaryWorktree);
        return File.Exists(resolvedPath) ? ReadDiskText(resolvedPath) : null;
    }

    private static TextAndVersion? LoadGeneratedText(
        RepoRoot targetRoot,
        string sourceRoot,
        string storedPath,
        string resolvedPath,
        string? inlineText,
        bool isSecondaryWorktree)
    {
        if (File.Exists(resolvedPath))
            return ReadDiskText(resolvedPath);

        if (inlineText is not null)
        {
            var expanded = CompilationBlueprint.UnmaskRootText(targetRoot, inlineText);
            return TextAndVersion.Create(SourceText.From(expanded, Encoding.UTF8, SourceHashAlgorithm.Sha256), VersionStamp.Create(), resolvedPath);
        }

        if (isSecondaryWorktree && !Path.IsPathRooted(storedPath))
        {
            var sourceFallback = Path.GetFullPath(Path.Combine(sourceRoot, storedPath.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(sourceFallback))
                return ReadDiskText(sourceFallback, resolvedPath);
        }

        return null;
    }

    private static TextAndVersion? ReadDiskText(string diskPath, string? virtualPath = null)
    {
        try
        {
            var bytes = SharedRead.AllBytes(diskPath);
            return TextAndVersion.Create(SolutionViews.Decode(bytes), VersionStamp.Create(), virtualPath ?? diskPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ResolveExistingPath(RepoRoot targetRoot, string sourceRoot, string storedPath, bool isSecondaryWorktree)
    {
        var resolved = CompilationBlueprint.ResolvePath(targetRoot, storedPath);
        if (File.Exists(resolved))
            return resolved;
        if (isSecondaryWorktree && !Path.IsPathRooted(storedPath))
        {
            var fromSource = Path.GetFullPath(Path.Combine(sourceRoot, storedPath.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(fromSource))
                return fromSource;
        }

        return null;
    }

    private static string? ResolveOutputPath(RepoRoot targetRoot, string? storedOutput)
    {
        if (string.IsNullOrEmpty(storedOutput))
            return null;
        return CompilationBlueprint.ResolvePath(targetRoot, storedOutput);
    }

    private static string[] RelativeFolders(string projectDirectory, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(dir))
            return [];
        var rel = Path.GetRelativePath(projectDirectory, dir);
        if (rel == "." || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return [];
        return rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    }

    private static CSharpCompilationOptions BuildCompilationOptions(CompilationBlueprint.BlueprintProject bp, RepoRoot targetRoot)
    {
        var outputKind = Enum.TryParse<OutputKind>(bp.OutputKind, out var ok) ? ok : OutputKind.DynamicallyLinkedLibrary;
        var nullable = Enum.TryParse<NullableContextOptions>(bp.Nullable, out var nc) ? nc : NullableContextOptions.Disable;
        var generalDiag = Enum.TryParse<ReportDiagnostic>(bp.GeneralDiagnosticOption, out var gd) ? gd : ReportDiagnostic.Default;
        var platform = Enum.TryParse<Platform>(bp.Platform, out var pl) ? pl : Platform.AnyCpu;
        var optimization = Enum.TryParse<OptimizationLevel>(bp.OptimizationLevel, out var ol) ? ol : OptimizationLevel.Debug;
        var importOptions = Enum.TryParse<MetadataImportOptions>(bp.MetadataImportOptions, out var mio) ? mio : MetadataImportOptions.Public;

        var specific = ImmutableDictionary.CreateBuilder<string, ReportDiagnostic>(StringComparer.Ordinal);
        foreach (var (id, diag) in bp.SpecificDiagnosticOptions)
        {
            if (Enum.TryParse<ReportDiagnostic>(diag, out var report))
                specific[id] = report;
        }

        var cryptoPublicKey = bp.CryptoPublicKey is null
            ? default(ImmutableArray<byte>)
            : ImmutableArray.CreateRange(Convert.FromHexString(bp.CryptoPublicKey));
        StrongNameProvider? strongNameProvider = string.Equals(bp.StrongNameProvider, typeof(DesktopStrongNameProvider).FullName, StringComparison.Ordinal)
            ? new DesktopStrongNameProvider()
            : null;

        return new CSharpCompilationOptions(
            outputKind: outputKind,
            reportSuppressedDiagnostics: false,
            moduleName: bp.ModuleName,
            mainTypeName: bp.MainTypeName,
            scriptClassName: bp.ScriptClassName,
            usings: bp.Usings is { Count: > 0 } ? bp.Usings : null,
            optimizationLevel: optimization,
            checkOverflow: bp.CheckOverflow,
            allowUnsafe: bp.AllowUnsafe,
            cryptoKeyContainer: bp.CryptoKeyContainer,
            cryptoKeyFile: string.IsNullOrEmpty(bp.CryptoKeyFile) ? null : CompilationBlueprint.ResolvePath(targetRoot, bp.CryptoKeyFile),
            cryptoPublicKey: cryptoPublicKey,
            delaySign: bp.DelaySign,
            strongNameProvider: strongNameProvider,
            platform: platform,
            generalDiagnosticOption: generalDiag,
            warningLevel: bp.WarningLevel,
            specificDiagnosticOptions: specific.ToImmutable(),
            concurrentBuild: true,
            deterministic: bp.Deterministic,
            xmlReferenceResolver: XmlFileResolver.Default,
            sourceReferenceResolver: SourceFileResolver.Default,
            // MSBuildWorkspace sets its own workspace-bound resolver here, whose type Roslyn keeps internal,
            // so no public API can build the same instance. It only resolves #r directives and missing
            // assemblies at emit, which a check never does, so the default stays.
            metadataReferenceResolver: null,
            assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default,
            nullableContextOptions: nullable)
            .WithMetadataImportOptions(importOptions);
    }

    private static CSharpParseOptions BuildParseOptions(CompilationBlueprint.BlueprintProject bp)
    {
        var lang = Enum.TryParse<LanguageVersion>(bp.LanguageVersion, out var lv) ? lv : LanguageVersion.Default;
        var docMode = Enum.TryParse<DocumentationMode>(bp.DocumentationMode, out var dm) ? dm : DocumentationMode.Parse;
        var options = new CSharpParseOptions(lang, docMode, SourceCodeKind.Regular, bp.DefineConstants);
        if (bp.Features is { Count: > 0 })
            options = options.WithFeatures(bp.Features);
        return options;
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
        var byKey = items.ToDictionary(i => (i.Path, i.Blueprint.Name));
        var visited = new HashSet<(RepoPath, string)>();
        var ordered = new List<(RepoPath, ProjectNode, CompilationBlueprint.BlueprintProject)>(items.Count);

        void Visit((RepoPath Path, ProjectNode Node, CompilationBlueprint.BlueprintProject Blueprint) item)
        {
            var key = (item.Path, item.Blueprint.Name);
            if (!visited.Add(key))
                return;
            foreach (var pr in item.Blueprint.ProjectReferences)
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

    /// <summary>
    ///     Process-wide immutable resource cache shared across the primary workspace and every worktree overlay so
    ///     metadata tables, analyzer assemblies and unchanged HEAD texts are materialized once.
    /// </summary>
    internal sealed class SharedResources
    {
        // At most this many HEAD texts stay cached; past it the least recently used goes.
        private const int MaxHeadTexts = 1024;

        private readonly IAnalyzerAssemblyLoader _analyzerLoader;
        private readonly ConcurrentDictionary<string, (long Length, long Ticks, MetadataReference Reference)> _metadata = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (long Length, long Ticks, AnalyzerFileReference Reference)> _analyzers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _headLock = new();
        private readonly Dictionary<(string Head, string Relative), LinkedListNode<HeadTextEntry>> _headTexts = [];
        private readonly LinkedList<HeadTextEntry> _headOrder = [];

        public SharedResources(IAnalyzerAssemblyLoader analyzerLoader) => _analyzerLoader = analyzerLoader;

        public int MetadataCount => _metadata.Count;

        public int HeadTextCount
        {
            get
            {
                lock (_headLock)
                    return _headTexts.Count;
            }
        }

        public MetadataReference? GetMetadataReference(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return null;
                var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
                if (_metadata.TryGetValue(path, out var kept) && kept.Length == stamp.Length && kept.Ticks == stamp.Ticks)
                    return kept.Reference;
                var reference = MetadataReference.CreateFromFile(path);
                _metadata[path] = (stamp.Length, stamp.Ticks, reference);
                return reference;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        public AnalyzerFileReference? GetAnalyzerReference(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return null;
                var stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
                if (_analyzers.TryGetValue(path, out var kept) && kept.Length == stamp.Length && kept.Ticks == stamp.Ticks)
                    return kept.Reference;
                _analyzerLoader.AddDependencyLocation(path);
                var reference = new AnalyzerFileReference(path, _analyzerLoader);
                _analyzers[path] = (stamp.Length, stamp.Ticks, reference);
                return reference;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        public TextAndVersion? GetOrReadHeadText(string head, string relativePath, string diskPath)
        {
            byte[] bytes;
            try
            {
                bytes = SharedRead.AllBytes(diskPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var key = (head, relativePath);
            lock (_headLock)
            {
                if (_headTexts.TryGetValue(key, out var node) && string.Equals(node.Value.ContentHash, hash, StringComparison.Ordinal))
                {
                    _headOrder.Remove(node);
                    _headOrder.AddFirst(node);
                    return TextAndVersion.Create(node.Value.Text, VersionStamp.Create(), diskPath);
                }

                var text = SolutionViews.Decode(bytes);
                if (node is not null)
                {
                    node.Value = new HeadTextEntry(key, hash, text);
                    _headOrder.Remove(node);
                    _headOrder.AddFirst(node);
                }
                else
                {
                    _headTexts[key] = _headOrder.AddFirst(new HeadTextEntry(key, hash, text));
                    while (_headTexts.Count > MaxHeadTexts && _headOrder.Last is { } last)
                    {
                        _headOrder.RemoveLast();
                        _headTexts.Remove(last.Value.Key);
                    }
                }

                return TextAndVersion.Create(text, VersionStamp.Create(), diskPath);
            }
        }

        public void TrimUnused(Solution? activeSolution)
        {
            lock (_headLock)
            {
                _headTexts.Clear();
                _headOrder.Clear();
            }
            if (activeSolution is null)
            {
                _metadata.Clear();
                return;
            }

            var inUse = new HashSet<MetadataReference>();
            foreach (var project in activeSolution.Projects)
            {
                foreach (var reference in project.MetadataReferences)
                    inUse.Add(reference);
            }

            foreach (var key in _metadata.Keys.ToList())
            {
                if (_metadata.TryGetValue(key, out var entry) && !inUse.Contains(entry.Reference))
                    _metadata.TryRemove(key, out _);
            }
        }

        private sealed record HeadTextEntry((string Head, string Relative) Key, string ContentHash, SourceText Text);
    }
}
