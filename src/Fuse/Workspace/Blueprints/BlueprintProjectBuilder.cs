using System.Collections.Immutable;
using System.Text;
using Fuse.Graph;
using Fuse.Paths;
using Fuse.Workspace.Views;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
namespace Fuse.Workspace.Blueprints;

/// <summary>
///     Builds Roslyn projects from blueprint entries. It turns each entry's stored paths and compiler
///     settings into a project with documents, references and options, so the workspace stays focused on
///     tracking which projects are open.
/// </summary>
internal sealed class BlueprintProjectBuilder
{
    private readonly BlueprintResourceCache _shared;

    public BlueprintProjectBuilder(BlueprintResourceCache shared)
    {
        _shared = shared;
    }

    /// <summary>
    ///     Builds the Roslyn project for <paramref name="blueprint"/> with documents, references and options,
    ///     resolving every stored path against <paramref name="targetRoot"/>.
    /// </summary>
    internal ProjectInfo BuildProjectInfo(
        ProjectId id,
        RepoRoot targetRoot,
        ProjectNode node,
        CompilationBlueprint.BlueprintProject blueprint,
        string sourceRoot,
        Dictionary<(RepoPath Path, string Name), ProjectId> projectIds,
        string? head,
        IReadOnlySet<RepoPath>? dirtyPaths,
        HashSet<RepoPath> knownSources,
        IReadOnlyDictionary<RepoPath, List<ProjectId>> fallbackByPath)
    {
        var projectFile = targetRoot.PathOf(CompilationBlueprint.ResolvePath(targetRoot, blueprint.Identity.ProjectPath)).Absolute;
        var projectDir = Path.GetDirectoryName(projectFile) ?? targetRoot.Path;
        var isSecondaryWorktree = !targetRoot.PathOf(sourceRoot).Equals(targetRoot.PathOf(targetRoot.Path));

        var projectReferences = new List<ProjectReference>(blueprint.References.Items.Count);
        foreach (var pr in blueprint.References.Items)
        {
            var resolvedTarget = CompilationBlueprint.ResolvePath(targetRoot, pr.TargetProjectPath);
            var targetPath = targetRoot.PathOf(resolvedTarget);
            if (projectIds.TryGetValue((targetPath, pr.TargetProjectName), out var targetId))
            {
                projectReferences.Add(new ProjectReference(targetId, [.. pr.Aliases], pr.EmbedInteropTypes));
            }
            else if (fallbackByPath.TryGetValue(targetPath, out var fallbackIds) && fallbackIds.Count > 0)
            {
                projectReferences.Add(new ProjectReference(fallbackIds[0], [.. pr.Aliases], pr.EmbedInteropTypes));
            }
            else if (!targetRoot.Contains(resolvedTarget))
            {
                throw new InvalidOperationException($"blueprint cannot load {blueprint.Identity.ProjectPath}: it references {pr.TargetProjectPath} outside the repository");
            }
        }

        var metadataReferences = new List<MetadataReference>(blueprint.Paths.ReferencePath.Count);
        foreach (var refPath in blueprint.Paths.ReferencePath)
        {
            var resolved = ResolveExistingPath(targetRoot, sourceRoot, refPath, isSecondaryWorktree);
            if (resolved is not null && _shared.GetMetadataReference(resolved) is { } metadata)
                metadataReferences.Add(metadata);
        }

        var analyzerReferences = new List<AnalyzerReference>(blueprint.Paths.Analyzer.Count);
        foreach (var analyzerPath in blueprint.Paths.Analyzer)
        {
            var resolved = ResolveExistingPath(targetRoot, sourceRoot, analyzerPath, isSecondaryWorktree);
            if (resolved is not null && _shared.GetAnalyzerReference(resolved) is { } analyzer)
                analyzerReferences.Add(analyzer);
        }

        var documents = new List<DocumentInfo>(blueprint.Paths.Compile.Count + node.Sources.Count);
        var seenDocPaths = new HashSet<RepoPath>();
        foreach (var doc in blueprint.Paths.Compile)
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

        var additional = BuildSupplementaryDocuments(id, targetRoot, sourceRoot, projectDir, blueprint.Paths.AdditionalFiles, isSecondaryWorktree);
        var analyzerConfigs = BuildSupplementaryDocuments(id, targetRoot, sourceRoot, projectDir, blueprint.Paths.AnalyzerConfigFiles, isSecondaryWorktree);

        var compilationOptions = BuildCompilationOptions(blueprint, targetRoot);
        var parseOptions = BuildParseOptions(blueprint);
        var outputPath = ResolveOutputPath(targetRoot, blueprint.Paths.OutputPath);
        var outputRefPath = ResolveOutputPath(targetRoot, blueprint.Paths.OutputRefPath);

        return ProjectInfo.Create(
                id,
                VersionStamp.Create(),
                blueprint.Identity.Name,
                blueprint.Identity.AssemblyName,
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

    /// <summary>
    ///     Builds supplementary documents (additional files or analyzer configs) for <paramref name="projectId"/>,
    ///     skipping the ones with no text on disk or inline.
    /// </summary>
    internal static List<DocumentInfo> BuildSupplementaryDocuments(
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
        if (canShareHead && head is not null)
            return _shared.GetOrReadHeadText(head, repoPath.Relative, resolvedPath);

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

    /// <summary>
    ///     Builds the Roslyn compilation options for <paramref name="blueprint"/>, falling back to defaults
    ///     when a stored spelling names no option the running Roslyn knows.
    /// </summary>
    internal static CSharpCompilationOptions BuildCompilationOptions(CompilationBlueprint.BlueprintProject blueprint, RepoRoot targetRoot)
    {
        var outputKind = Enum.TryParse<OutputKind>(blueprint.Settings.OutputKind, out var ok) ? ok : OutputKind.DynamicallyLinkedLibrary;
        var nullable = Enum.TryParse<NullableContextOptions>(blueprint.Settings.Nullable, out var nc) ? nc : NullableContextOptions.Disable;
        var generalDiag = Enum.TryParse<ReportDiagnostic>(blueprint.Settings.GeneralDiagnosticOption, out var gd) ? gd : ReportDiagnostic.Default;
        var platform = Enum.TryParse<Platform>(blueprint.Settings.Platform, out var pl) ? pl : Platform.AnyCpu;
        var optimization = Enum.TryParse<OptimizationLevel>(blueprint.Settings.OptimizationLevel, out var ol) ? ol : OptimizationLevel.Debug;
        var importOptions = Enum.TryParse<MetadataImportOptions>(blueprint.Settings.MetadataImportOptions, out var mio) ? mio : MetadataImportOptions.Public;

        var specific = ImmutableDictionary.CreateBuilder<string, ReportDiagnostic>(StringComparer.Ordinal);
        foreach (var (id, diag) in blueprint.Settings.SpecificDiagnosticOptions)
        {
            if (Enum.TryParse<ReportDiagnostic>(diag, out var report))
                specific[id] = report;
        }

        var cryptoPublicKey = blueprint.Settings.CryptoPublicKey is null
            ? default(ImmutableArray<byte>)
            : ImmutableArray.CreateRange(Convert.FromHexString(blueprint.Settings.CryptoPublicKey));
        StrongNameProvider? strongNameProvider = string.Equals(blueprint.Settings.StrongNameProvider, typeof(DesktopStrongNameProvider).FullName, StringComparison.Ordinal)
            ? new DesktopStrongNameProvider()
            : null;

        return new CSharpCompilationOptions(
            outputKind: outputKind,
            reportSuppressedDiagnostics: false,
            moduleName: blueprint.Settings.ModuleName,
            mainTypeName: blueprint.Settings.MainTypeName,
            scriptClassName: blueprint.Settings.ScriptClassName,
            usings: blueprint.Settings.Usings is { Count: > 0 } ? blueprint.Settings.Usings : null,
            optimizationLevel: optimization,
            checkOverflow: blueprint.Settings.CheckOverflow,
            allowUnsafe: blueprint.Settings.AllowUnsafe,
            cryptoKeyContainer: blueprint.Settings.CryptoKeyContainer,
            cryptoKeyFile: string.IsNullOrEmpty(blueprint.Paths.CryptoKeyFile) ? null : CompilationBlueprint.ResolvePath(targetRoot, blueprint.Paths.CryptoKeyFile),
            cryptoPublicKey: cryptoPublicKey,
            delaySign: blueprint.Settings.DelaySign,
            strongNameProvider: strongNameProvider,
            platform: platform,
            generalDiagnosticOption: generalDiag,
            warningLevel: blueprint.Settings.WarningLevel,
            specificDiagnosticOptions: specific.ToImmutable(),
            concurrentBuild: true,
            deterministic: blueprint.Settings.Deterministic,
            xmlReferenceResolver: XmlFileResolver.Default,
            sourceReferenceResolver: SourceFileResolver.Default,
            metadataReferenceResolver: null,
            assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default,
            nullableContextOptions: nullable)
            .WithMetadataImportOptions(importOptions);
    }

    /// <summary>
    ///     Builds the Roslyn parse options for <paramref name="blueprint"/>, falling back to defaults when a
    ///     stored spelling names no option the running Roslyn knows.
    /// </summary>
    internal static CSharpParseOptions BuildParseOptions(CompilationBlueprint.BlueprintProject blueprint)
    {
        var lang = Enum.TryParse<LanguageVersion>(blueprint.Settings.LanguageVersion, out var lv) ? lv : LanguageVersion.Default;
        var docMode = Enum.TryParse<DocumentationMode>(blueprint.Settings.DocumentationMode, out var dm) ? dm : DocumentationMode.Parse;
        var options = new CSharpParseOptions(lang, docMode, SourceCodeKind.Regular, blueprint.Settings.DefineConstants);
        if (blueprint.Settings.Features is { Count: > 0 })
            options = options.WithFeatures(blueprint.Settings.Features);
        return options;
    }
}
