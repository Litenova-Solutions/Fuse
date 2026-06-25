using Fuse.Collection;
using Fuse.Collection.Models;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Fusion;

/// <summary>
///     Describes the active scoping mode on a fusion request for explain and preview surfaces.
/// </summary>
public static class FusionScopeDescriptor
{
    /// <summary>
    ///     Returns a short human-readable description of the active scope on <paramref name="request" />.
    /// </summary>
    /// <param name="request">The fusion request whose focus, query, or change options are described.</param>
    /// <returns>A scope summary string suitable for explain output headers.</returns>
    public static string Describe(FusionRequest request)
    {
        if (request.Focus is not null)
            return $"focus '{request.Focus.Seed}' depth {request.Focus.Depth}";
        if (request.Query is not null)
            return $"query '{request.Query.Query}' top {request.Query.TopFiles} depth {request.Query.Depth}";
        if (request.Changes is not null)
            return $"changed since '{request.Changes.Since}'";
        return "all collected files";
    }

    /// <summary>
    ///     Applies a host or MCP scope mode to a request builder and returns the normalized mode name.
    /// </summary>
    /// <param name="builder">The request builder to configure.</param>
    /// <param name="mode">The requested mode: <c>focus</c>, <c>changes</c>, or anything else for <c>search</c>.</param>
    /// <param name="seed">The focus seed when <paramref name="mode" /> is <c>focus</c>.</param>
    /// <param name="query">The search query when the mode resolves to <c>search</c>.</param>
    /// <param name="since">The git base when <paramref name="mode" /> is <c>changes</c>.</param>
    /// <param name="depth">Dependency traversal depth for focus and search modes.</param>
    /// <param name="queryTop">Number of top-ranked seed files for search mode.</param>
    /// <returns>The normalized scoping mode applied to <paramref name="builder" />.</returns>
    public static string ApplyMode(
        FusionRequestBuilder builder,
        string? mode,
        string? seed,
        string? query,
        string? since,
        int depth = 2,
        int queryTop = 10)
    {
        var normalized = (mode ?? "search").Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "focus" when !string.IsNullOrWhiteSpace(seed):
                builder.WithFocusOptions(new FocusOptions(seed, depth));
                break;
            case "changes" when !string.IsNullOrWhiteSpace(since):
                builder.WithChangeOptions(new ChangeOptions(since));
                break;
            default:
                normalized = "search";
                builder.WithQueryOptions(new QueryOptions(query ?? string.Empty, queryTop, depth));
                break;
        }

        return normalized;
    }
}
