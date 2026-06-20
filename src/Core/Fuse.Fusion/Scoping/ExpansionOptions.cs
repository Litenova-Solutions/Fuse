namespace Fuse.Fusion.Scoping;

/// <summary>
///     Controls how seed paths are expanded across the dependency graph.
/// </summary>
/// <param name="Depth">The maximum number of hops to traverse from any seed. <c>0</c> returns only the seeds.</param>
/// <param name="FollowReferences">
///     When <see langword="true" />, forward edges are followed: a file pulls in the files that define the
///     types it references (its dependencies).
/// </param>
/// <param name="FollowDependents">
///     When <see langword="true" />, reverse edges are followed: a file pulls in the files that reference the
///     types it declares (its dependents).
/// </param>
/// <param name="HopDecay">
///     The factor by which a neighbour's score decays per hop from its parent, in the range (0, 1]. Lower
///     values make distant files rank well below the seeds, so they are dropped first under a token budget.
/// </param>
/// <param name="TokenBudget">
///     An optional soft cap on the estimated tokens admitted during expansion. Seeds are always admitted;
///     once the budget is reached no further neighbours are added. <c>null</c> disables budget gating.
/// </param>
/// <param name="TokenCosts">
///     Per-path estimated token costs used with <paramref name="TokenBudget" />. Paths absent from the map
///     cost nothing. Ignored when <paramref name="TokenBudget" /> is <c>null</c>.
/// </param>
public sealed record ExpansionOptions(
    int Depth,
    bool FollowReferences = true,
    bool FollowDependents = false,
    double HopDecay = 0.5,
    int? TokenBudget = null,
    IReadOnlyDictionary<string, int>? TokenCosts = null);
