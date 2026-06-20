namespace Fuse.Fusion.Scoping;

/// <summary>
///     Result of dependency-aware path expansion with provenance chains and relevance scores.
/// </summary>
/// <param name="IncludedPaths">All paths included after expansion.</param>
/// <param name="ProvenanceChains">
///     Maps each included path to the hop chain from seed to inclusion (inclusive).
/// </param>
/// <param name="Scores">
///     Maps each included path to its relevance score. Seeds carry their seed score; expanded files carry a
///     score decayed by hop distance. Used to order emission so the most relevant files survive a token
///     budget.
/// </param>
public sealed record PathExpansionResult(
    HashSet<string> IncludedPaths,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ProvenanceChains,
    IReadOnlyDictionary<string, double> Scores);
