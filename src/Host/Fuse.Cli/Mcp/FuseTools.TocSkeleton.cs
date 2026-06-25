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
    ///     Emits a table of contents for a .NET codebase: a directory tree with per-file token costs and a
    ///     symbol outline. The cheapest first call for surveying an unfamiliar codebase.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Absolute or relative path to the source directory.</param>
    /// <param name="excludeDirectories">Directory names to skip, or <see langword="null" /> for none.</param>
    /// <param name="excludeFiles">File names to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludePatterns">Glob patterns to exclude, or <see langword="null" /> for none.</param>
    /// <param name="excludeTestProjects">When <see langword="true" />, skip all test project directories.</param>
    /// <param name="maxTokens">
    ///     The largest table of contents to return. On a codebase whose full map would exceed this, the document
    ///     degrades (drops symbol outlines, then collapses to directory aggregates) so the result stays inline
    ///     rather than overflowing the tool-result size cap.
    /// </param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The table-of-contents document as a single string, or a descriptive error message when the directory
    ///     is missing or fusion fails.
    /// </returns>
    [McpServerTool(Name = "fuse_toc", ReadOnly = true)]
    [Description("Emits a table of contents (directory tree, symbol outline, and per-file token costs) for a .NET codebase. The cheapest first call when exploring: prefer this over grepping or opening files blindly to survey the codebase, then fetch specific files with fuse_focus or fuse_search. On a large codebase the map degrades to fit maxTokens (drops outlines, then collapses to directories) instead of overflowing.")]
    public static Task<string> FuseTocAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [Description("Absolute or relative path to the source directory.")] string path,
        [Description("Directory names to skip.")] string[]? excludeDirectories = null,
        [Description("File names to exclude.")] string[]? excludeFiles = null,
        [Description("Glob patterns to exclude.")] string[]? excludePatterns = null,
        [Description("Exclude all test project directories.")] bool excludeTestProjects = false,
        [Description("Largest table of contents to return, in tokens. The map degrades to fit rather than overflow. Defaults to 24000.")] int maxTokens = 24000,
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
                        ShowTokenCount = false,
                        IncludeManifest = false,
                        TableOfContents = true,
                        TableOfContentsMaxTokens = maxTokens > 0 ? maxTokens : 24000,
                    })
                    .WithReductionOptions(new ReductionOptions(enableRedaction: true));

                FuseToolHelpers.ApplyCommonFilters(
                    builder,
                    null, null, null,
                    excludeDirectories, excludeFiles, excludePatterns,
                    excludeTestProjects: excludeTestProjects);
            },
            trackTopTokenFiles: false,
            cancellationToken);
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
    /// <param name="publicApi">When <see langword="true" />, emit only public and protected member skeletons.</param>
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
        [Description("Emit only public and protected member skeletons.")] bool publicApi = false,
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
                        level: publicApi ? ReductionLevel.PublicApi : ReductionLevel.Skeleton,
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
}
