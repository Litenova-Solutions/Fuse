namespace Fuse.Scoping;

/// <summary>
///     A plan describing which files to include in a context payload, at what render tier, why, and at what
///     estimated token cost. The renderer (a later phase) turns this into the emitted payload.
/// </summary>
/// <param name="Mode">The plan mode (for example <c>context</c> or <c>review</c>).</param>
/// <param name="Items">The included files, ranked.</param>
/// <param name="ExplanationEdges">The semantic edges that explain why non-seed files were included.</param>
/// <param name="EstimatedTokens">The total estimated token cost of the included items.</param>
/// <param name="Warnings">Warnings (for example low signal, or items dropped to fit the budget).</param>
public sealed record ContextPlan(
    string Mode,
    IReadOnlyList<ContextPlanItem> Items,
    IReadOnlyList<ContextPlanEdge> ExplanationEdges,
    int EstimatedTokens,
    IReadOnlyList<string> Warnings);

/// <summary>
///     A single file in a context plan.
/// </summary>
/// <param name="Path">The file's normalized path.</param>
/// <param name="NodeId">A representative node id for the file, or null.</param>
/// <param name="Role">The file's role (for example <c>exact-seed</c>, <c>di-implementation</c>, <c>test</c>).</param>
/// <param name="Tier">The render tier to emit the file at.</param>
/// <param name="Score">The retrieval score.</param>
/// <param name="EstimatedTokens">The estimated token cost at the chosen tier.</param>
/// <param name="MustKeep">Whether the file must be included regardless of budget.</param>
/// <param name="Reasons">Human-readable inclusion reasons.</param>
/// <param name="ProvenanceChain">The edge chain that brought the file in.</param>
public sealed record ContextPlanItem(
    string Path,
    string? NodeId,
    string Role,
    RenderTier Tier,
    double Score,
    int EstimatedTokens,
    bool MustKeep,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> ProvenanceChain);

/// <summary>
///     An edge included in a plan to explain why a file was pulled in.
/// </summary>
/// <param name="From">The source node id.</param>
/// <param name="To">The target node id.</param>
/// <param name="EdgeType">The edge type.</param>
/// <param name="Weight">The edge weight.</param>
/// <param name="Evidence">The edge evidence, when available.</param>
public sealed record ContextPlanEdge(
    string From,
    string To,
    string EdgeType,
    double Weight,
    string? Evidence);

/// <summary>
///     The tier at which a file is rendered into a context payload.
/// </summary>
public enum RenderTier
{
    /// <summary>The full source text.</summary>
    FullSource,

    /// <summary>Reduced source (bodies trimmed, signatures kept).</summary>
    Reduced,

    /// <summary>Signatures only, no bodies.</summary>
    Skeleton,

    /// <summary>Public API surface only.</summary>
    PublicApi,

    /// <summary>A brief structural sketch.</summary>
    Sketch,

    /// <summary>Listed but not rendered.</summary>
    Omitted,
}
