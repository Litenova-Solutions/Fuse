using System.ComponentModel;
using Fuse.Analysis.Changes;
using Fuse.Analysis.Dependencies;
using Fuse.Analysis.Search;
using Fuse.Collection.Models;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP tool definitions for Fuse, exposed to AI agents through the Model Context Protocol server.
/// </summary>
/// <remarks>
///     Each method maps to an MCP tool whose name is set by <see cref="McpServerToolAttribute" /> (for example
///     <c>fuse_skeleton</c>). Every parameter maps to an MCP tool argument the agent supplies; the parameter
///     <c>[Description]</c> attributes are the agent-facing schema descriptions. All tools are read-only, run
///     fusion in memory (no files are written), and return errors as descriptive strings rather than throwing.
/// </remarks>
[McpServerToolType]
public sealed class FuseTools
{
    /// <summary>
    ///     Emits a structural skeleton of a .NET codebase for low-token architecture review.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="semanticMarkers">When <see langword="true" />, prepend structural annotation comments to each entry.</param>
    /// <param name="all">When <see langword="true" />, apply all C# reduction options. Defaults to <see langword="true" /> for the smallest skeleton.</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The skeleton fusion output (signatures without bodies) as a single string, or a descriptive error
    ///     message when the directory is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_skeleton", ReadOnly = true)]
    [Description("Emits structural skeleton only (signatures, no bodies) for a .NET codebase. Use for cold-start architecture review.")]
    public static Task<string> FuseSkeletonAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("Prepend structural annotation comments.")] bool semanticMarkers = false,
        [Description("Apply all C# reduction options.")] bool all = true,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        CancellationToken cancellationToken = default) =>
        FuseToolHelpers.ExecuteDotNetAsync(
            orchestrator,
            templateRegistry,
            path,
            builder =>
            {
                builder
                    .WithEmissionOptions(new EmissionOptions
                    {
                        MaxTokens = maxTokens,
                        ShowTokenCount = false,
                        TrackTopTokenFiles = trackTopTokenFiles,
                        IncludeManifest = true
                    })
                    .WithReductionOptions(new ReductionOptions(
                        removeCSharpComments: all,
                        removeCSharpUsings: all,
                        removeCSharpNamespaces: all,
                        removeCSharpRegions: all,
                        aggressiveCSharpReduction: all,
                        skeletonMode: true,
                        includeSemanticMarkers: semanticMarkers,
                        enableRedaction: true));

                FuseToolHelpers.ApplyCommonFilters(
                    builder,
                    null, null, null,
                    excludeDirectories, excludeFiles, excludePatterns,
                    excludeTestProjects: excludeTestProjects);
            },
            trackTopTokenFiles,
            cancellationToken);

    /// <summary>
    ///     Scopes fusion to a type, file, or directory and its dependencies.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="focus">Type name, filename, or path used as the focus seed.</param>
    /// <param name="depth">Number of dependency hops to traverse out from the seed.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="all">When <see langword="true" />, apply all C# reduction options.</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The focus-scoped fusion output as a single string, or a descriptive error message when the directory
    ///     is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_focus", ReadOnly = true)]
    [Description("Scopes fusion to a type name, filename, or path plus dependency traversal. Use after skeleton review to drill into an area.")]
    public static Task<string> FuseFocusAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Type name, filename, or path to scope around.")] string focus,
        [Description("Dependency traversal depth.")] int depth = 1,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("Apply all C# reduction options.")] bool all = false,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        CancellationToken cancellationToken = default) =>
        FuseToolHelpers.ExecuteDotNetAsync(
            orchestrator,
            templateRegistry,
            path,
            builder =>
            {
                builder
                    .WithEmissionOptions(new EmissionOptions
                    {
                        MaxTokens = maxTokens,
                        ShowTokenCount = false,
                        TrackTopTokenFiles = trackTopTokenFiles,
                        IncludeManifest = true
                    })
                    .WithReductionOptions(new ReductionOptions(
                        removeCSharpComments: all,
                        removeCSharpUsings: all,
                        removeCSharpNamespaces: all,
                        removeCSharpRegions: all,
                        aggressiveCSharpReduction: all,
                        enableRedaction: true))
                    .WithFocusOptions(new FocusOptions(focus, depth));

                FuseToolHelpers.ApplyCommonFilters(
                    builder,
                    null, null, null,
                    excludeDirectories, excludeFiles, excludePatterns,
                    excludeTestProjects: excludeTestProjects);
            },
            trackTopTokenFiles,
            cancellationToken);

    /// <summary>
    ///     Scopes fusion to files ranked by BM25 relevance to a query.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="query">Natural-language or keyword query ranked with BM25.</param>
    /// <param name="queryTop">Number of top-ranked files used to seed dependency expansion.</param>
    /// <param name="depth">Number of dependency hops to traverse out from the seed files.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="all">When <see langword="true" />, apply all C# reduction options.</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The query-scoped fusion output as a single string, or a descriptive error message when the directory
    ///     is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_search", ReadOnly = true)]
    [Description("BM25 query-scoped fusion for a .NET codebase. Returns the most relevant files plus dependency expansion.")]
    public static Task<string> FuseSearchAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Natural-language or keyword query.")] string query,
        [Description("Number of top-ranked seed files.")] int queryTop = 10,
        [Description("Dependency traversal depth after seed selection.")] int depth = 1,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("Apply all C# reduction options.")] bool all = false,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        CancellationToken cancellationToken = default) =>
        FuseToolHelpers.ExecuteDotNetAsync(
            orchestrator,
            templateRegistry,
            path,
            builder =>
            {
                builder
                    .WithEmissionOptions(new EmissionOptions
                    {
                        MaxTokens = maxTokens,
                        ShowTokenCount = false,
                        TrackTopTokenFiles = trackTopTokenFiles,
                        IncludeManifest = true
                    })
                    .WithReductionOptions(new ReductionOptions(
                        removeCSharpComments: all,
                        removeCSharpUsings: all,
                        removeCSharpNamespaces: all,
                        removeCSharpRegions: all,
                        aggressiveCSharpReduction: all,
                        enableRedaction: true))
                    .WithQueryOptions(new QueryOptions(query, queryTop, depth));

                FuseToolHelpers.ApplyCommonFilters(
                    builder,
                    null, null, null,
                    excludeDirectories, excludeFiles, excludePatterns,
                    excludeTestProjects: excludeTestProjects);
            },
            trackTopTokenFiles,
            cancellationToken);

    /// <summary>
    ///     Scopes fusion to files changed since a git ref.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="changedSince">Git ref (branch, commit, or <c>HEAD~N</c>) to diff the working tree against.</param>
    /// <param name="includeDependents">When <see langword="true" />, also include first-degree dependents of the changed files.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="all">When <see langword="true" />, apply all C# reduction options.</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The change-scoped fusion output as a single string, or a descriptive error message when the directory
    ///     is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_changes", ReadOnly = true)]
    [Description("Change-scoped fusion for a .NET codebase. Returns files changed since a git ref plus optional dependents.")]
    public static Task<string> FuseChangesAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Git ref (branch, commit, HEAD~N) to diff against.")] string changedSince,
        [Description("Include first-degree dependents of changed files.")] bool includeDependents = true,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("Apply all C# reduction options.")] bool all = false,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        CancellationToken cancellationToken = default) =>
        FuseToolHelpers.ExecuteDotNetAsync(
            orchestrator,
            templateRegistry,
            path,
            builder =>
            {
                builder
                    .WithEmissionOptions(new EmissionOptions
                    {
                        MaxTokens = maxTokens,
                        ShowTokenCount = false,
                        TrackTopTokenFiles = trackTopTokenFiles,
                        IncludeManifest = true
                    })
                    .WithReductionOptions(new ReductionOptions(
                        removeCSharpComments: all,
                        removeCSharpUsings: all,
                        removeCSharpNamespaces: all,
                        removeCSharpRegions: all,
                        aggressiveCSharpReduction: all,
                        enableRedaction: true))
                    .WithChangeOptions(new ChangeOptions(changedSince, includeDependents));

                FuseToolHelpers.ApplyCommonFilters(
                    builder,
                    null, null, null,
                    excludeDirectories, excludeFiles, excludePatterns,
                    excludeTestProjects: excludeTestProjects);
            },
            trackTopTokenFiles,
            cancellationToken);

    /// <summary>
    ///     Fuses a .NET codebase and returns optimized in-memory context.
    /// </summary>
    /// <remarks>
    ///     The full-control tool that exposes every .NET fusion option. <paramref name="focus" />,
    ///     <paramref name="changedSince" />, and <paramref name="query" /> are applied only when non-empty, and
    ///     may be combined.
    /// </remarks>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="includeExtensions">Extensions to add on top of the DotNet template defaults.</param>
    /// <param name="excludeExtensions">Extensions to remove from the DotNet template defaults.</param>
    /// <param name="onlyExtensions">Extensions to use exclusively, ignoring template defaults.</param>
    /// <param name="maxFileSizeKb">Maximum file size in KB; <c>0</c> means unlimited.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="excludeUnitTestProjects">When <see langword="true" />, skip only unit test project directories.</param>
    /// <param name="removeCSharpComments">When <see langword="true" />, strip C# comments.</param>
    /// <param name="removeCSharpUsings">When <see langword="true" />, strip C# using directives.</param>
    /// <param name="removeCSharpNamespaces">When <see langword="true" />, strip C# namespace declarations.</param>
    /// <param name="removeCSharpRegions">When <see langword="true" />, strip C# <c>#region</c> directives.</param>
    /// <param name="aggressive">When <see langword="true" />, apply aggressive C# reduction.</param>
    /// <param name="all">When <see langword="true" />, enable every C# reduction option regardless of the individual flags.</param>
    /// <param name="skeleton">When <see langword="true" />, emit a structural skeleton only.</param>
    /// <param name="semanticMarkers">When <see langword="true" />, prepend structural annotation comments to each entry.</param>
    /// <param name="focus">Type name, filename, or path to scope around, or <see langword="null" /> to skip focus scoping.</param>
    /// <param name="depth">Dependency traversal depth applied to focus and query scoping.</param>
    /// <param name="changedSince">Git ref to scope to changed files, or <see langword="null" /> to skip change scoping.</param>
    /// <param name="includeDependents">When <see langword="true" />, include first-degree dependents of changed files.</param>
    /// <param name="query">BM25 query to scope fusion, or <see langword="null" /> to skip query scoping.</param>
    /// <param name="queryTop">Number of top-ranked seed files for query scoping.</param>
    /// <param name="patternSummary">When <see langword="true" />, detect and append a cross-codebase pattern summary.</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="gitStats">When <see langword="true" />, include git churn stats in the manifest.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The XML-formatted fused file contents as a single string, or a descriptive error message when the
    ///     directory is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_dotnet", ReadOnly = true)]
    [Description("Full-control .NET fusion with all options (skeleton, focus, change scoping, pattern summary). Returns XML-formatted file contents.")]
    public static async Task<string> FuseDotNetAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Extensions to add on top of DotNet template defaults.")] string[]? includeExtensions = null,
        [Description("Extensions to remove from DotNet template defaults.")] string[]? excludeExtensions = null,
        [Description("Extensions to use exclusively, ignoring template defaults.")] string[]? onlyExtensions = null,
        [Description("Maximum file size in KB. Zero means unlimited.")] int maxFileSizeKb = 0,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("Exclude only unit test project directories.")] bool excludeUnitTestProjects = false,
        [Description("Remove C# comments.")] bool removeCSharpComments = false,
        [Description("Remove C# using directives.")] bool removeCSharpUsings = false,
        [Description("Remove C# namespace declarations.")] bool removeCSharpNamespaces = false,
        [Description("Remove C# region directives.")] bool removeCSharpRegions = false,
        [Description("Apply aggressive C# reduction.")] bool aggressive = false,
        [Description("Apply all reduction options at once.")] bool all = false,
        [Description("Emit structural skeleton only.")] bool skeleton = false,
        [Description("Prepend structural annotation comments.")] bool semanticMarkers = false,
        [Description("Type name, filename, or path to scope around.")] string? focus = null,
        [Description("Dependency traversal depth.")] int depth = 1,
        [Description("Git ref to scope to changed files.")] string? changedSince = null,
        [Description("Include first-degree dependents of changed files.")] bool includeDependents = true,
        [Description("BM25 query to scope fusion.")] string? query = null,
        [Description("Number of top-ranked seed files for query scoping.")] int queryTop = 10,
        [Description("Detect and append pattern summary.")] bool patternSummary = false,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        [Description("Include git churn stats in the manifest.")] bool gitStats = false,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = Path.GetFullPath(path);
        if (!Directory.Exists(resolvedPath))
            return $"Error: Directory not found: {resolvedPath}";

        try
        {
            var builder = FuseToolHelpers.CreateDotNetBuilder(templateRegistry, resolvedPath)
                .WithMaxFileSizeKb(maxFileSizeKb)
                .WithCollectionBehavior(
                    excludeTestProjects: excludeTestProjects,
                    excludeUnitTestProjects: excludeUnitTestProjects)
                .WithEmissionOptions(new EmissionOptions
                {
                    MaxTokens = maxTokens,
                    ShowTokenCount = false,
                    TrackTopTokenFiles = trackTopTokenFiles,
                    IncludeManifest = true,
                    IncludeGitStats = gitStats
                })
                .WithReductionOptions(new ReductionOptions(
                    removeCSharpComments: all || removeCSharpComments,
                    removeCSharpUsings: all || removeCSharpUsings,
                    removeCSharpNamespaces: all || removeCSharpNamespaces,
                    removeCSharpRegions: all || removeCSharpRegions,
                    aggressiveCSharpReduction: all || aggressive,
                    skeletonMode: skeleton,
                    includeSemanticMarkers: semanticMarkers,
                    includePatternSummary: patternSummary,
                    enableRedaction: true));

            if (!string.IsNullOrWhiteSpace(focus))
                builder.WithFocusOptions(new FocusOptions(focus, depth));

            if (!string.IsNullOrWhiteSpace(changedSince))
                builder.WithChangeOptions(new ChangeOptions(changedSince, includeDependents));

            if (!string.IsNullOrWhiteSpace(query))
                builder.WithQueryOptions(new QueryOptions(query, queryTop, depth));

            FuseToolHelpers.ApplyOptionalFilters(
                builder, onlyExtensions, includeExtensions, excludeExtensions,
                excludeDirectories, excludeFiles, excludePatterns);

            return await FuseToolHelpers.ExecuteInMemoryAsync(
                orchestrator, builder.Build(), trackTopTokenFiles, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error during fusion: {ex.Message}";
        }
    }

    /// <summary>
    ///     Fuses a codebase using a named template and returns optimized in-memory context.
    /// </summary>
    /// <remarks>
    ///     Use this for any non-.NET template (Python, Go, Rust, and so on). When <paramref name="template" /> is
    ///     <see langword="null" /> or empty, the orchestrator infers a template from the directory contents.
    /// </remarks>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="template">Template name (for example <c>Python</c>, <c>Go</c>, <c>Rust</c>), or <see langword="null" /> to infer.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="includeExtensions">Extensions to add on top of the template defaults.</param>
    /// <param name="excludeExtensions">Extensions to remove from the template defaults.</param>
    /// <param name="onlyExtensions">Extensions to use exclusively, ignoring template defaults.</param>
    /// <param name="maxFileSizeKb">Maximum file size in KB; <c>0</c> means unlimited.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="changedSince">Git ref to scope to changed files, or <see langword="null" /> to skip change scoping.</param>
    /// <param name="includeDependents">When <see langword="true" />, include first-degree dependents of changed files.</param>
    /// <param name="maxTokens">Hard token limit at which emission stops, or <see langword="null" /> for unlimited.</param>
    /// <param name="trackTopTokenFiles">When <see langword="true" />, append the top token-consuming files to the trailing stats comment.</param>
    /// <param name="gitStats">When <see langword="true" />, include git churn stats in the manifest.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The XML-formatted fused file contents as a single string, or a descriptive error message when the
    ///     directory is missing, the template name is unknown, or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_generic", ReadOnly = true)]
    [Description("Generates optimized codebase context for any template (equivalent to fuse). Returns XML-formatted file contents.")]
    public static async Task<string> FuseGenericAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Template name: Python, JavaScript, TypeScript, Go, Rust, Java, etc.")] string? template = null,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Extensions to add on top of template defaults.")] string[]? includeExtensions = null,
        [Description("Extensions to remove from template defaults.")] string[]? excludeExtensions = null,
        [Description("Extensions to use exclusively.")] string[]? onlyExtensions = null,
        [Description("Maximum file size in KB. Zero means unlimited.")] int maxFileSizeKb = 0,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("Git ref to scope to changed files.")] string? changedSince = null,
        [Description("Include first-degree dependents of changed files.")] bool includeDependents = true,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        [Description("Include git churn stats in the manifest.")] bool gitStats = false,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = Path.GetFullPath(path);
        if (!Directory.Exists(resolvedPath))
            return $"Error: Directory not found: {resolvedPath}";

        ProjectTemplate? parsedTemplate = null;
        if (!string.IsNullOrWhiteSpace(template))
        {
            if (!Enum.TryParse<ProjectTemplate>(template, ignoreCase: true, out var t))
            {
                return $"Error: Unknown template '{template}'. Valid values: {string.Join(", ", Enum.GetNames<ProjectTemplate>())}";
            }

            parsedTemplate = t;
        }

        try
        {
            var builder = new FusionRequestBuilder(templateRegistry)
                .WithSourceDirectory(resolvedPath)
                .WithInMemory(true)
                .WithMaxFileSizeKb(maxFileSizeKb)
                .WithCollectionBehavior(excludeTestProjects: excludeTestProjects)
                .WithEmissionOptions(new EmissionOptions
                {
                    MaxTokens = maxTokens,
                    ShowTokenCount = false,
                    TrackTopTokenFiles = trackTopTokenFiles,
                    IncludeManifest = true,
                    IncludeGitStats = gitStats
                })
                .WithReductionOptions(new ReductionOptions(enableRedaction: true));

            if (parsedTemplate.HasValue)
                builder.WithTemplate(parsedTemplate.Value);

            if (!string.IsNullOrWhiteSpace(changedSince))
                builder.WithChangeOptions(new ChangeOptions(changedSince, includeDependents));

            FuseToolHelpers.ApplyOptionalFilters(
                builder, onlyExtensions, includeExtensions, excludeExtensions,
                excludeDirectories, excludeFiles, excludePatterns);

            return await FuseToolHelpers.ExecuteInMemoryAsync(
                orchestrator, builder.Build(), trackTopTokenFiles, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error during fusion: {ex.Message}";
        }
    }
}
