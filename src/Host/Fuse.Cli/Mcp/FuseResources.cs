using Fuse.Fusion.Scoping;
using Fuse.Collection.Models;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Plugins.Abstractions.Options;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP resource definitions for Fuse, exposed to AI agents through the Model Context Protocol server.
/// </summary>
/// <remarks>
///     Each method backs an MCP resource addressed by the <c>fuse://</c> URI templates declared on
///     <see cref="McpServerResourceAttribute" />. The <c>{template}</c>, <c>{path}</c>, <c>{seed}</c>,
///     <c>{query}</c>, and <c>{since}</c> URI segments bind to the matching method parameters. All resources run
///     fusion in memory (no files are written) and return errors as descriptive strings rather than throwing.
///     The scoped resources mirror the equivalent <see cref="FuseTools" /> tools with fixed default options.
/// </remarks>
[McpServerResourceType]
public sealed class FuseResources
{
    /// <summary>
    ///     Reads fused content for a given template and path using default options.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves template defaults.</param>
    /// <param name="template">Template name from the URI (<c>dotnet</c>, <c>python</c>, <c>generic</c>, <c>azuredevopswiki</c>).</param>
    /// <param name="path">Relative path to the directory to fuse.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The fused content as a single string, or a descriptive error message when the directory is missing,
    ///     the template name is unknown, or fusion fails.
    /// </returns>
    [McpServerResource(
        UriTemplate = "fuse://{template}/{path}",
        Name = "Fused Codebase Context",
        MimeType = "text/plain")]
    [System.ComponentModel.Description("Returns the optimized, minified content of a codebase directory.")]
    public static async Task<string> ReadFuseResourceAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [System.ComponentModel.Description("The project template (dotnet, python, generic, azuredevopswiki).")] string template,
        [System.ComponentModel.Description("Relative path to the directory to fuse.")] string path,
        CancellationToken cancellationToken = default) =>
        await ExecuteFusionAsync(orchestrator, templateRegistry, template, path, _ => { }, cancellationToken);

    /// <summary>
    ///     Reads a skeleton overview of a .NET codebase. Equivalent to the skeleton workflow.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Path to the directory to fuse.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The structural skeleton (signatures only) as a single string, or a descriptive error message when the
    ///     directory is missing or fusion fails.
    /// </returns>
    [McpServerResource(
        UriTemplate = "fuse://skeleton/{path}",
        Name = "Skeleton Codebase Overview",
        MimeType = "text/plain")]
    [System.ComponentModel.Description("Returns a structural skeleton (signatures only) for a .NET codebase. Equivalent to fuse_skeleton.")]
    public static Task<string> ReadSkeletonResourceAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [System.ComponentModel.Description("Path to the directory to fuse.")] string path,
        CancellationToken cancellationToken = default) =>
        ExecuteFusionAsync(
            orchestrator,
            templateRegistry,
            "dotnet",
            path,
            builder => builder
                .WithReductionOptions(new ReductionOptions(
                    level: ReductionLevel.Skeleton,
                    enableRedaction: true)),
            cancellationToken);

    /// <summary>
    ///     Reads focus-scoped fused content for a .NET codebase. Equivalent to the focus workflow
    ///     with a dependency depth of one.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Path to the directory to fuse.</param>
    /// <param name="seed">Type name, filename, or path used as the focus seed.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The focus-scoped fused content as a single string, or a descriptive error message when the directory
    ///     is missing or fusion fails.
    /// </returns>
    [McpServerResource(
        UriTemplate = "fuse://focus/{path}/{seed}",
        Name = "Focus-Scoped Codebase Context",
        MimeType = "text/plain")]
    [System.ComponentModel.Description("Returns fused content scoped to a type, file, or path plus dependencies. Equivalent to fuse_focus.")]
    public static Task<string> ReadFocusResourceAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [System.ComponentModel.Description("Path to the directory to fuse.")] string path,
        [System.ComponentModel.Description("Type name, filename, or path seed.")] string seed,
        CancellationToken cancellationToken = default) =>
        ExecuteFusionAsync(
            orchestrator,
            templateRegistry,
            "dotnet",
            path,
            builder => builder.WithFocusOptions(new FocusOptions(seed, Depth: 1)),
            cancellationToken);

    /// <summary>
    ///     Reads query-scoped fused content for a .NET codebase. Equivalent to the search workflow
    ///     with the top ten files and a dependency depth of one.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Path to the directory to fuse.</param>
    /// <param name="query">BM25 query string used to rank files.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The query-scoped fused content as a single string, or a descriptive error message when the directory
    ///     is missing or fusion fails.
    /// </returns>
    [McpServerResource(
        UriTemplate = "fuse://search/{path}/{query}",
        Name = "Query-Scoped Codebase Context",
        MimeType = "text/plain")]
    [System.ComponentModel.Description("Returns BM25 query-scoped fused content. Equivalent to fuse_search.")]
    public static Task<string> ReadSearchResourceAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [System.ComponentModel.Description("Path to the directory to fuse.")] string path,
        [System.ComponentModel.Description("BM25 query string.")] string query,
        CancellationToken cancellationToken = default) =>
        ExecuteFusionAsync(
            orchestrator,
            templateRegistry,
            "dotnet",
            path,
            builder => builder.WithQueryOptions(new QueryOptions(query, TopFiles: 10, Depth: 1)),
            cancellationToken);

    /// <summary>
    ///     Reads change-scoped fused content for a .NET codebase. Equivalent to the change-scoped workflow
    ///     with dependents included.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator that runs the pipeline.</param>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="path">Path to the directory to fuse.</param>
    /// <param name="since">Git ref (branch, commit, or <c>HEAD~N</c>) to diff against.</param>
    /// <param name="cancellationToken">Token used to cancel the fusion run.</param>
    /// <returns>
    ///     The change-scoped fused content as a single string, or a descriptive error message when the directory
    ///     is missing or fusion fails.
    /// </returns>
    [McpServerResource(
        UriTemplate = "fuse://changes/{path}/{since}",
        Name = "Change-Scoped Codebase Context",
        MimeType = "text/plain")]
    [System.ComponentModel.Description("Returns fused content for files changed since a git ref. Equivalent to fuse_changes.")]
    public static Task<string> ReadChangesResourceAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        [System.ComponentModel.Description("Path to the directory to fuse.")] string path,
        [System.ComponentModel.Description("Git ref to diff against (branch, commit, HEAD~N).")] string since,
        CancellationToken cancellationToken = default) =>
        ExecuteFusionAsync(
            orchestrator,
            templateRegistry,
            "dotnet",
            path,
            builder => builder.WithChangeOptions(new ChangeOptions(since, IncludeDependents: true)),
            cancellationToken);

    private static async Task<string> ExecuteFusionAsync(
        FusionOrchestrator orchestrator,
        Fuse.Collection.Templates.ProjectTemplateRegistry templateRegistry,
        string template,
        string path,
        Action<FusionRequestBuilder> configure,
        CancellationToken cancellationToken)
    {
        var resolvedPath = Path.GetFullPath(path);

        if (!Directory.Exists(resolvedPath))
            return $"Error: Directory not found: {resolvedPath}";

        ProjectTemplate? parsedTemplate = null;
        if (!string.Equals(template, "generic", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(template))
        {
            if (!Enum.TryParse<ProjectTemplate>(template, ignoreCase: true, out var t))
            {
                return $"Error: Unknown template '{template}'. Valid values: generic, {string.Join(", ", Enum.GetNames<ProjectTemplate>())}";
            }

            parsedTemplate = t;
        }

        try
        {
            var builder = new FusionRequestBuilder(templateRegistry)
                .WithSourceDirectory(resolvedPath)
                .WithInMemory(true)
                .WithEmissionOptions(new EmissionOptions
                {
                    ShowTokenCount = false,
                    IncludeManifest = true
                })
                .WithReductionOptions(new ReductionOptions(enableRedaction: true));

            if (parsedTemplate.HasValue)
                builder.WithTemplate(parsedTemplate.Value);

            configure(builder);

            var result = await orchestrator.FuseAsync(builder.Build(), cancellationToken);

            if (string.IsNullOrEmpty(result.InMemoryContent))
                return "No files found matching the criteria.";

            return result.InMemoryContent;
        }
        catch (Exception ex)
        {
            return $"Error during fusion: {ex.Message}";
        }
    }
}
