namespace Fuse.Fusion.Scoping;

/// <summary>
///     Result of dependency-aware path expansion with optional provenance chains.
/// </summary>
/// <param name="IncludedPaths">All paths included after expansion.</param>
/// <param name="ProvenanceChains">
///     Maps each included path to the hop chain from seed to inclusion (inclusive).
/// </param>
public sealed record PathExpansionResult(
    HashSet<string> IncludedPaths,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ProvenanceChains);
