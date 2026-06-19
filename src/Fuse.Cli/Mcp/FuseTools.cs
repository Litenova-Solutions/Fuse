using System.ComponentModel;
using Fuse.Analysis.Changes;
using Fuse.Analysis.Dependencies;
using Fuse.Collection.Models;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Reduction.Options;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP tool definitions for Fuse.
/// </summary>
[McpServerToolType]
public sealed class FuseTools
{
    /// <summary>
    ///     Fuses a .NET codebase and returns optimized in-memory context.
    /// </summary>
    [McpServerTool(Name = "fuse_dotnet", ReadOnly = true)]
    [Description("Generates optimized .NET codebase context (equivalent to fuse dotnet). Returns XML-formatted file contents.")]
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
        [Description("Detect and append pattern summary.")] bool patternSummary = false,
        [Description("Hard token limit.")] int? maxTokens = null,
        [Description("Include top token-consuming files in the stats comment.")] bool trackTopTokenFiles = false,
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = Path.GetFullPath(path);
        if (!Directory.Exists(resolvedPath))
        {
            return $"Error: Directory not found: {resolvedPath}";
        }

        try
        {
            var builder = new FusionRequestBuilder(templateRegistry)
                .WithSourceDirectory(resolvedPath)
                .WithTemplate(ProjectTemplate.DotNet)
                .WithInMemory(true)
                .WithMaxFileSizeKb(maxFileSizeKb)
                .WithCollectionBehavior(
                    excludeTestProjects: excludeTestProjects,
                    excludeUnitTestProjects: excludeUnitTestProjects)
                .WithEmissionOptions(new EmissionOptions
                {
                    MaxTokens = maxTokens,
                    ShowTokenCount = false,
                    TrackTopTokenFiles = trackTopTokenFiles
                })
                .WithReductionOptions(new ReductionOptions(
                    removeCSharpComments: all || removeCSharpComments,
                    removeCSharpUsings: all || removeCSharpUsings,
                    removeCSharpNamespaces: all || removeCSharpNamespaces,
                    removeCSharpRegions: all || removeCSharpRegions,
                    aggressiveCSharpReduction: all || aggressive,
                    skeletonMode: skeleton,
                    includeSemanticMarkers: semanticMarkers,
                    includePatternSummary: patternSummary));

            if (!string.IsNullOrWhiteSpace(focus))
            {
                builder.WithFocusOptions(new FocusOptions(focus, depth));
            }

            if (!string.IsNullOrWhiteSpace(changedSince))
            {
                builder.WithChangeOptions(new ChangeOptions(changedSince, includeDependents));
            }

            ApplyOptionalFilters(builder, onlyExtensions, includeExtensions, excludeExtensions, excludeDirectories, excludeFiles, excludePatterns);

            return await ExecuteInMemoryAsync(orchestrator, builder.Build(), trackTopTokenFiles, cancellationToken);
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
        CancellationToken cancellationToken = default)
    {
        var resolvedPath = Path.GetFullPath(path);
        if (!Directory.Exists(resolvedPath))
        {
            return $"Error: Directory not found: {resolvedPath}";
        }

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
                    TrackTopTokenFiles = trackTopTokenFiles
                });

            if (parsedTemplate.HasValue)
            {
                builder.WithTemplate(parsedTemplate.Value);
            }

            if (!string.IsNullOrWhiteSpace(changedSince))
            {
                builder.WithChangeOptions(new ChangeOptions(changedSince, includeDependents));
            }

            ApplyOptionalFilters(builder, onlyExtensions, includeExtensions, excludeExtensions, excludeDirectories, excludeFiles, excludePatterns);

            return await ExecuteInMemoryAsync(orchestrator, builder.Build(), trackTopTokenFiles, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error during fusion: {ex.Message}";
        }
    }

    private static void ApplyOptionalFilters(
        FusionRequestBuilder builder,
        string[]? onlyExtensions,
        string[]? includeExtensions,
        string[]? excludeExtensions,
        string[]? excludeDirectories,
        string[]? excludeFiles,
        string[]? excludePatterns)
    {
        if (onlyExtensions?.Length > 0)
        {
            builder.WithOnlyExtensions(NormalizeExtensions(onlyExtensions));
        }

        if (includeExtensions?.Length > 0)
        {
            builder.WithIncludeExtensions(NormalizeExtensions(includeExtensions));
        }

        if (excludeExtensions?.Length > 0)
        {
            builder.WithExcludeExtensions(NormalizeExtensions(excludeExtensions));
        }

        if (excludeDirectories?.Length > 0)
        {
            builder.WithExcludeDirectories(excludeDirectories);
        }

        if (excludeFiles?.Length > 0)
        {
            builder.WithExcludeFiles(excludeFiles);
        }

        if (excludePatterns?.Length > 0)
        {
            builder.WithExcludePatterns(excludePatterns);
        }
    }

    private static string[] NormalizeExtensions(IEnumerable<string> extensions) =>
        extensions.Select(e => e.StartsWith('.') ? e : $".{e}").ToArray();

    private static async Task<string> ExecuteInMemoryAsync(
        FusionOrchestrator orchestrator,
        FusionRequest request,
        bool trackTopTokenFiles,
        CancellationToken cancellationToken)
    {
        var result = await orchestrator.FuseAsync(request, cancellationToken);

        if (string.IsNullOrEmpty(result.InMemoryContent))
        {
            return "No files found matching the criteria.";
        }

        if (!trackTopTokenFiles)
        {
            return result.InMemoryContent;
        }

        var top = result.TopTokenFiles.Count > 0
            ? string.Join(", ", result.TopTokenFiles.Select(f =>
                $"{Path.GetFileName(f.Path)} ({FormatTokenCount(f.Count)})"))
            : "none";

        var statsLine =
            $"\n<!-- fuse: {result.ProcessedFileCount}/{result.TotalFileCount} files | ~{FormatTokenCount(result.TotalTokens)} tokens | {result.Duration.TotalSeconds:F1}s | top: {top} -->";

        return result.InMemoryContent + statsLine;
    }

    private static string FormatTokenCount(long count) =>
        count >= 1000 ? $"{count / 1000.0:F0}k" : count.ToString();
}
