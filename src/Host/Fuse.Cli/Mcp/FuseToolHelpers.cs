using Fuse.Analysis.Changes;
using Fuse.Analysis.Dependencies;
using Fuse.Analysis.Search;
using Fuse.Collection.Models;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Shared helpers for Fuse MCP tool implementations.
/// </summary>
internal static class FuseToolHelpers
{
    internal static async Task<string> ExecuteDotNetAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        string path,
        Action<FusionRequestBuilder> configure,
        bool trackTopTokenFiles,
        CancellationToken cancellationToken)
    {
        var resolvedPath = Path.GetFullPath(path);
        if (!Directory.Exists(resolvedPath))
            return $"Error: Directory not found: {resolvedPath}";

        try
        {
            var builder = CreateDotNetBuilder(templateRegistry, resolvedPath);
            configure(builder);
            return await ExecuteInMemoryAsync(orchestrator, builder.Build(), trackTopTokenFiles, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"Error during fusion: {ex.Message}";
        }
    }

    internal static FusionRequestBuilder CreateDotNetBuilder(
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        string resolvedPath) =>
        new FusionRequestBuilder(templateRegistry)
            .WithSourceDirectory(resolvedPath)
            .WithTemplate(ProjectTemplate.DotNet)
            .WithInMemory(true)
            .WithEmissionOptions(new EmissionOptions
            {
                MaxTokens = null,
                ShowTokenCount = false,
                IncludeManifest = true
            })
            .WithReductionOptions(new ReductionOptions(enableRedaction: true));

    internal static void ApplyCommonFilters(
        FusionRequestBuilder builder,
        string[]? onlyExtensions,
        string[]? includeExtensions,
        string[]? excludeExtensions,
        string[]? excludeDirectories,
        string[]? excludeFiles,
        string[]? excludePatterns,
        int maxFileSizeKb = 0,
        bool excludeTestProjects = false,
        bool excludeUnitTestProjects = false)
    {
        if (maxFileSizeKb > 0)
            builder.WithMaxFileSizeKb(maxFileSizeKb);

        if (excludeTestProjects || excludeUnitTestProjects)
        {
            builder.WithCollectionBehavior(
                excludeTestProjects: excludeTestProjects,
                excludeUnitTestProjects: excludeUnitTestProjects);
        }

        ApplyOptionalFilters(builder, onlyExtensions, includeExtensions, excludeExtensions, excludeDirectories, excludeFiles, excludePatterns);
    }

    internal static void ApplyOptionalFilters(
        FusionRequestBuilder builder,
        string[]? onlyExtensions,
        string[]? includeExtensions,
        string[]? excludeExtensions,
        string[]? excludeDirectories,
        string[]? excludeFiles,
        string[]? excludePatterns)
    {
        if (onlyExtensions?.Length > 0)
            builder.WithOnlyExtensions(NormalizeExtensions(onlyExtensions));

        if (includeExtensions?.Length > 0)
            builder.WithIncludeExtensions(NormalizeExtensions(includeExtensions));

        if (excludeExtensions?.Length > 0)
            builder.WithExcludeExtensions(NormalizeExtensions(excludeExtensions));

        if (excludeDirectories?.Length > 0)
            builder.WithExcludeDirectories(excludeDirectories);

        if (excludeFiles?.Length > 0)
            builder.WithExcludeFiles(excludeFiles);

        if (excludePatterns?.Length > 0)
            builder.WithExcludePatterns(excludePatterns);
    }

    internal static string[] NormalizeExtensions(IEnumerable<string> extensions) =>
        extensions.Select(e => e.StartsWith('.') ? e : $".{e}").ToArray();

    internal static async Task<string> ExecuteInMemoryAsync(
        FusionOrchestrator orchestrator,
        FusionRequest request,
        bool trackTopTokenFiles,
        CancellationToken cancellationToken)
    {
        var result = await orchestrator.FuseAsync(request, cancellationToken);

        if (string.IsNullOrEmpty(result.InMemoryContent))
            return "No files found matching the criteria.";

        if (!trackTopTokenFiles)
            return result.InMemoryContent;

        var top = result.TopTokenFiles.Count > 0
            ? string.Join(", ", result.TopTokenFiles.Select(f =>
                $"{Path.GetFileName(f.Path)} ({FormatTokenCount(f.Count)})"))
            : "none";

        var statsLine =
            $"\n<!-- fuse: {result.ProcessedFileCount}/{result.TotalFileCount} files | ~{FormatTokenCount(result.TotalTokens)} tokens | {result.Duration.TotalSeconds:F1}s | top: {top} -->";

        return result.InMemoryContent + statsLine;
    }

    internal static string FormatTokenCount(long count) =>
        count >= 1000 ? $"{count / 1000.0:F0}k" : count.ToString();
}
