using System.ComponentModel;
using Fuse.Fusion.Scoping;
using Fuse.Collection.Models;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction.Security;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

public sealed partial class FuseTools
{
    /// <summary>
    ///     Fuses a .NET codebase and returns optimized in-memory context.
    /// </summary>
    /// <remarks>
    ///     The full-control tool that exposes every .NET fusion option. <paramref name="focus" />,
    ///     <paramref name="changedSince" />, and <paramref name="query" /> are mutually exclusive scoping modes.
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
    /// <param name="level">The C# reduction level: none, standard, aggressive, skeleton, or publicApi.</param>
    /// <param name="semanticMarkers">When <see langword="true" />, prepend structural annotation comments to each entry.</param>
    /// <param name="focus">Type name, filename, or path to scope around, or <see langword="null" /> to skip focus scoping.</param>
    /// <param name="depth">Dependency traversal depth applied to focus and query scoping.</param>
    /// <param name="changedSince">Git ref to scope to changed files, or <see langword="null" /> to skip change scoping.</param>
    /// <param name="includeDependents">When <see langword="true" />, include first-degree dependents of changed files.</param>
    /// <param name="query">BM25 query to scope fusion, or <see langword="null" /> to skip query scoping.</param>
    /// <param name="queryTop">Number of top-ranked seed files for query scoping.</param>
    /// <param name="patternSummary">When <see langword="true" />, detect and append a cross-codebase pattern summary.</param>
    /// <param name="collapseGenerated">When <see langword="true" />, collapse EF Core migration and model-snapshot bodies to signatures.</param>
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
        [Description("C# reduction level: none, standard, aggressive, skeleton, publicApi. Defaults to standard.")] ReductionLevel level = ReductionLevel.Standard,
        [Description("Prepend structural annotation comments.")] bool semanticMarkers = false,
        [Description("Type name, filename, or path to scope around.")] string? focus = null,
        [Description("Dependency traversal depth.")] int depth = 1,
        [Description("Git ref to scope to changed files.")] string? changedSince = null,
        [Description("Include first-degree dependents of changed files.")] bool includeDependents = true,
        [Description("BM25 query to scope fusion.")] string? query = null,
        [Description("Number of top-ranked seed files for query scoping.")] int queryTop = 10,
        [Description("Detect and append pattern summary.")] bool patternSummary = false,
        [Description("Collapse EF Core migration and model-snapshot bodies to signatures.")] bool collapseGenerated = false,
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
                    level: level,
                    includeSemanticMarkers: semanticMarkers,
                    includePatternSummary: patternSummary,
                    collapseGeneratedCode: collapseGenerated,
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
        catch (FusionException ex)
        {
            return $"Error: {ex.Message}";
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

    /// <summary>
    ///     Compacts a caller-supplied set of files, or raw content, by running Fuse's reduction without
    ///     collecting a whole directory. Lets an agent shrink context it has already identified.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Base directory for resolving relative <paramref name="files" /> paths.</param>
    /// <param name="files">Explicit file paths to reduce; mutually exclusive with <paramref name="content" />.</param>
    /// <param name="content">Raw content to reduce instead of files; uses <paramref name="extension" /> to pick the reducer.</param>
    /// <param name="extension">The extension that selects the reducer for <paramref name="content" />.</param>
    /// <param name="level">The C# reduction level to apply.</param>
    /// <param name="maxTokens">Token ceiling for the output, or zero for no limit.</param>
    /// <param name="cancellationToken">Token used to cancel the run.</param>
    /// <returns>The reduced output, or a descriptive error message when the input is invalid or fusion fails.</returns>
    [McpServerTool(Name = "fuse_reduce", ReadOnly = true)]
    [Description("Compacts a specific set of files (or raw content) by running Fuse's reduction, without collecting a whole directory. Pass `files` (paths you already identified) or `content` (+ `extension`). Use to shrink context before working with it, when fuse_search or fuse_focus is too broad.")]
    public static Task<string> FuseReduceAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Base directory for resolving relative file paths. Ignored in content mode.")] string path = ".",
        [Description("File paths to reduce, absolute or relative to path.")] string[]? files = null,
        [Description("Raw content to reduce instead of files. Provide extension to select the reducer.")] string? content = null,
        [Description("Extension that selects the reducer for content (for example .cs, .ts, .py). Defaults to .cs.")] string extension = ".cs",
        [Description("C# reduction level: none, standard, aggressive, skeleton, publicApi. Defaults to standard.")] ReductionLevel level = ReductionLevel.Standard,
        [Description("Maximum tokens the reduced output may use, or 0 for no limit.")] int maxTokens = 0,
        CancellationToken cancellationToken = default)
    {
        int? maxTokenLimit = maxTokens > 0 ? maxTokens : null;

        if (!string.IsNullOrEmpty(content))
            return ReduceRunner.ReduceContentAsync(
                orchestrator, templateRegistry, content, extension, level, maxTokenLimit, cancellationToken);

        if (files is { Length: > 0 })
            return ReduceRunner.ReduceFilesAsync(
                orchestrator, templateRegistry, path, files, level, maxTokenLimit, cancellationToken);

        return Task.FromResult("Error: provide either files (paths) or content to reduce.");
    }
}
