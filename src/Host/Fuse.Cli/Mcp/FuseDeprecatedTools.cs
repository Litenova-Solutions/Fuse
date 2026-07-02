using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     Deprecation shims for the Fuse V2 MCP tool names that V3 renamed. Each retired name is still a
///     registered tool that returns a short, actionable message naming its V3 replacement.
/// </summary>
/// <remarks>
///     The shims exist so that a version upgrade is self-announcing across the tool rename. A client that
///     cached the V2 tool surface (for example a long-lived agent session whose Fuse server was upgraded
///     underneath) calls a retired name and gets clear guidance instead of an opaque <c>Unknown tool</c>
///     JSON-RPC error. They deliberately do no work and never touch the index; each accepts the common V2
///     arguments only so a stale call still binds, then ignores them. Routing to the V3 tool was rejected on
///     purpose: the V3 tools have different contracts (for example <c>fuse_localize</c> returns no bodies
///     where <c>fuse_search</c> returned source), so silently re-pointing a call would change output shape
///     without the caller knowing. Remove this type in the next major once V2 callers are gone.
/// </remarks>
[McpServerToolType]
public sealed class FuseDeprecatedTools
{
    /// <summary>Deprecated V2 alias for <c>fuse_map</c>.</summary>
    /// <param name="path">Ignored. Accepted so a V2 call still binds.</param>
    /// <returns>Guidance naming the V3 replacement.</returns>
    [McpServerTool(Name = "fuse_toc", ReadOnly = true)]
    [Description("Deprecated: renamed to fuse_map in Fuse V3. Call fuse_map instead.")]
    public static string FuseToc([Description("Ignored.")] string? path = null) =>
        Renamed("fuse_toc", "fuse_map", "the workspace map of symbols, routes, and counts");

    /// <summary>Deprecated V2 alias for <c>fuse_map</c> / <c>fuse_context</c>.</summary>
    /// <param name="path">Ignored. Accepted so a V2 call still binds.</param>
    /// <returns>Guidance naming the V3 replacement.</returns>
    [McpServerTool(Name = "fuse_skeleton", ReadOnly = true)]
    [Description("Deprecated: removed in Fuse V3. Use fuse_map for structure, or fuse_context for skeleton-tier source.")]
    public static string FuseSkeleton([Description("Ignored.")] string? path = null) =>
        Renamed("fuse_skeleton", "fuse_map", "the workspace structure; use fuse_context with selected seeds for skeleton-tier source");

    /// <summary>Deprecated V2 alias for <c>fuse_localize</c> (then <c>fuse_context</c>).</summary>
    /// <param name="path">Ignored. Accepted so a V2 call still binds.</param>
    /// <param name="query">Ignored. Accepted so a V2 call still binds.</param>
    /// <returns>Guidance naming the V3 replacement.</returns>
    [McpServerTool(Name = "fuse_search", ReadOnly = true)]
    [Description("Deprecated: replaced in Fuse V3. Use fuse_localize to rank candidates, then fuse_context to read them.")]
    public static string FuseSearch([Description("Ignored.")] string? path = null, [Description("Ignored.")] string? query = null) =>
        Renamed("fuse_search", "fuse_localize", "ranked candidate files; follow with fuse_context to read the source bodies");

    /// <summary>Deprecated V2 alias for <c>fuse_context</c> / <c>fuse_neighbors</c>.</summary>
    /// <param name="path">Ignored. Accepted so a V2 call still binds.</param>
    /// <param name="focus">Ignored. Accepted so a V2 call still binds.</param>
    /// <returns>Guidance naming the V3 replacement.</returns>
    [McpServerTool(Name = "fuse_focus", ReadOnly = true)]
    [Description("Deprecated: replaced in Fuse V3. Use fuse_context to emit source for a seed, or fuse_neighbors to explore its graph.")]
    public static string FuseFocus([Description("Ignored.")] string? path = null, [Description("Ignored.")] string? focus = null) =>
        Renamed("fuse_focus", "fuse_context", "scoped source for a seed; use fuse_neighbors to walk its dependency neighborhood");

    /// <summary>Deprecated V2 alias for <c>fuse_review</c>.</summary>
    /// <param name="path">Ignored. Accepted so a V2 call still binds.</param>
    /// <param name="changedSince">Ignored. Accepted so a V2 call still binds.</param>
    /// <returns>Guidance naming the V3 replacement.</returns>
    [McpServerTool(Name = "fuse_changes", ReadOnly = true)]
    [Description("Deprecated: renamed to fuse_review in Fuse V3. Call fuse_review instead.")]
    public static string FuseChanges([Description("Ignored.")] string? path = null, [Description("Ignored.")] string? changedSince = null) =>
        Renamed("fuse_changes", "fuse_review", "diff-first change impact and packed context");

    /// <summary>Deprecated V2 alias for <c>fuse_localize</c>.</summary>
    /// <param name="path">Ignored. Accepted so a V2 call still binds.</param>
    /// <param name="task">Ignored. Accepted so a V2 call still binds.</param>
    /// <returns>Guidance naming the V3 replacement.</returns>
    [McpServerTool(Name = "fuse_ask", ReadOnly = true)]
    [Description("Deprecated: replaced in Fuse V3. Use fuse_localize for a task, then fuse_context to pack the selected seeds to a budget.")]
    public static string FuseAsk([Description("Ignored.")] string? path = null, [Description("Ignored.")] string? task = null) =>
        Renamed("fuse_ask", "fuse_localize", "ranked candidates for a task; follow with fuse_context for budget-packed source");

    /// <summary>Deprecated V2 alias for <c>fuse_context</c>.</summary>
    /// <param name="path">Ignored. Accepted so a V2 call still binds.</param>
    /// <returns>Guidance naming the V3 replacement.</returns>
    [McpServerTool(Name = "fuse_dotnet", ReadOnly = true)]
    [Description("Deprecated: removed in Fuse V3. Use fuse_context (seeds) or fuse_review (changes) for packed source.")]
    public static string FuseDotnet([Description("Ignored.")] string? path = null) =>
        Renamed("fuse_dotnet", "fuse_context", "packed source for seeds; use fuse_review for change-scoped context");

    /// <summary>Deprecated V2 alias with no V3 equivalent (V3 is .NET-only for full fusion).</summary>
    /// <param name="path">Ignored. Accepted so a V2 call still binds.</param>
    /// <returns>Guidance naming the closest V3 replacement.</returns>
    [McpServerTool(Name = "fuse_generic", ReadOnly = true)]
    [Description("Deprecated: removed in Fuse V3. Use fuse_reduce for a known set of files, or fuse_context over the indexed workspace.")]
    public static string FuseGeneric([Description("Ignored.")] string? path = null) =>
        Renamed("fuse_generic", "fuse_reduce", "compaction of a known set of files; use fuse_context to pull from the indexed workspace");

    // The single message shape for every retired name, so an upgraded server answers a stale call with a
    // clear pointer to the current tool rather than a bare Unknown tool error.
    private static string Renamed(string oldName, string newName, string what) =>
        $"Error: the MCP tool '{oldName}' was removed in Fuse V3. Use '{newName}' instead ({what}). " +
        "This usually means your client cached an older Fuse tool list; reconnect the Fuse MCP server to refresh it. " +
        "See https://fuse.codes/docs/reference/mcp-tools.";
}
