namespace Fuse.Retrieval;

/// <summary>
///     A request to plan a context payload from a set of seeds.
/// </summary>
/// <param name="RootDirectory">The workspace root.</param>
/// <param name="Seeds">The seeds to build context around.</param>
/// <param name="Depth">The graph expansion depth.</param>
/// <param name="MaxTokens">An optional token budget; must-keep items are always included.</param>
/// <param name="RenderMode">The requested render mode.</param>
/// <param name="IncludeTests">Whether test files are included.</param>
/// <param name="IncludeConfig">Whether config files are included.</param>
public sealed record ContextRequest(
    string RootDirectory,
    IReadOnlyList<ContextSeed> Seeds,
    int Depth = 2,
    int? MaxTokens = null,
    ContextRenderMode RenderMode = ContextRenderMode.Mixed,
    bool IncludeTests = true,
    bool IncludeConfig = true);

/// <summary>
///     A single context seed: what to build context around.
/// </summary>
/// <param name="Kind">The seed kind.</param>
/// <param name="Value">The seed value (a path, symbol name, route, service, request, or config section).</param>
public sealed record ContextSeed(ContextSeedKind Kind, string Value);

/// <summary>The kind of a context seed.</summary>
public enum ContextSeedKind
{
    /// <summary>A file path.</summary>
    File,

    /// <summary>A symbol name.</summary>
    Symbol,

    /// <summary>A service type name.</summary>
    Service,

    /// <summary>A request or command type name.</summary>
    Request,

    /// <summary>A route ("METHOD /pattern").</summary>
    Route,

    /// <summary>A configuration section name.</summary>
    Config,
}

/// <summary>
///     How a context payload should be rendered.
/// </summary>
public enum ContextRenderMode
{
    /// <summary>Full source for all files.</summary>
    Source,

    /// <summary>Reduced source for all files.</summary>
    Reduced,

    /// <summary>Skeletons for all files.</summary>
    Skeleton,

    /// <summary>Public API for all files.</summary>
    PublicApi,

    /// <summary>Mixed tiers chosen per role.</summary>
    Mixed,
}

/// <summary>
///     A request to review the semantic impact of a change.
/// </summary>
/// <param name="RootDirectory">The workspace root.</param>
/// <param name="ChangedSince">The git base ref to diff against.</param>
/// <param name="Depth">The graph expansion depth for the blast radius.</param>
/// <param name="MaxTokens">An optional token budget; changed files are always kept.</param>
/// <param name="IncludeTests">Whether related test files are included.</param>
/// <param name="IncludeConfig">Whether related config files are included.</param>
public sealed record ReviewRequest(
    string RootDirectory,
    string ChangedSince,
    int Depth = 2,
    int? MaxTokens = null,
    bool IncludeTests = true,
    bool IncludeConfig = true);

/// <summary>
///     The result of localizing a task: ranked candidates with reasons and token costs, no source bodies.
/// </summary>
/// <param name="Candidates">The ranked candidates.</param>
/// <param name="Warnings">Warnings (for example low signal).</param>
public sealed record LocalizationResult(
    IReadOnlyList<LocalizedCandidate> Candidates,
    IReadOnlyList<string> Warnings);

/// <summary>
///     A single localized candidate (no source body).
/// </summary>
/// <param name="Path">The candidate's normalized file path.</param>
/// <param name="NodeId">The graph node id, or empty for a file-only candidate.</param>
/// <param name="Kind">The candidate kind.</param>
/// <param name="Score">The candidate score.</param>
/// <param name="EstimatedTokens">The estimated token cost to read the file.</param>
/// <param name="Reasons">The inclusion reasons.</param>
public sealed record LocalizedCandidate(
    string Path,
    string NodeId,
    string Kind,
    double Score,
    int EstimatedTokens,
    IReadOnlyList<string> Reasons);
