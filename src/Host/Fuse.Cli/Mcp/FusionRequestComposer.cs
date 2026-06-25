using Fuse.Collection.Models;
using Fuse.Collection.Templates;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Shared fusion request builder defaults for CLI commands, MCP tools, and the VS Code host RPC surface.
/// </summary>
public static class FusionRequestComposer
{
    /// <summary>
    ///     Creates an in-memory .NET fusion request builder with MCP-friendly defaults.
    /// </summary>
    /// <param name="templateRegistry">Registry that resolves the <c>dotnet</c> template defaults.</param>
    /// <param name="resolvedPath">Absolute path to the source directory.</param>
    /// <returns>A preconfigured builder; callers add scope, filters, and emission overrides.</returns>
    public static FusionRequestBuilder CreateDotNetInMemoryBuilder(
        ProjectTemplateRegistry templateRegistry,
        string resolvedPath) =>
        new FusionRequestBuilder(templateRegistry)
            .WithSourceDirectory(resolvedPath)
            .WithTemplate(ProjectTemplate.DotNet)
            .WithInMemory(true)
            .WithPersistentIndex(true)
            .WithEmissionOptions(new EmissionOptions
            {
                MaxTokens = null,
                ShowTokenCount = false,
                IncludeManifest = true,
            })
            .WithReductionOptions(new ReductionOptions(enableRedaction: true));

    /// <summary>
    ///     Applies mutually exclusive focus, query, or change scoping to a builder.
    /// </summary>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="focus">Focus seed, or empty to skip.</param>
    /// <param name="query">Search query, or empty to skip when focus is absent.</param>
    /// <param name="changedSince">Git ref for change scoping, or empty to skip when focus and query are absent.</param>
    /// <param name="depth">Dependency depth for focus and query scoping.</param>
    /// <param name="queryTop">Top-ranked seed count for query scoping.</param>
    /// <param name="includeDependents">Whether change scoping includes first-degree dependents.</param>
    /// <param name="review">Whether change scoping prepends a review map.</param>
    public static void ApplyExclusiveScope(
        FusionRequestBuilder builder,
        string? focus,
        string? query,
        string? changedSince,
        int depth = 1,
        int queryTop = 10,
        bool includeDependents = true,
        bool review = false)
    {
        if (!string.IsNullOrWhiteSpace(focus))
            builder.WithFocusOptions(new FocusOptions(focus, depth));
        else if (!string.IsNullOrWhiteSpace(query))
            builder.WithQueryOptions(new QueryOptions(query, queryTop, depth));
        else if (!string.IsNullOrWhiteSpace(changedSince))
            builder.WithChangeOptions(new ChangeOptions(changedSince, includeDependents || review, review));
    }
}
