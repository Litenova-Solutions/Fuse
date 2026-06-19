using System.ComponentModel;
using Fuse.Analysis.Changes;
using Fuse.Analysis.Dependencies;
using Fuse.Analysis.Search;
using Fuse.Collection.Models;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Languages.Abstractions.Options;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP tool definitions for Fuse.
/// </summary>
[McpServerToolType]
public sealed class FuseTools
{
    /// <summary>
    ///     Emits a structural skeleton of a .NET codebase for low-token architecture review.
    /// </summary>
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
