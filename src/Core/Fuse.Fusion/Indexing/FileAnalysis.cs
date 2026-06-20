namespace Fuse.Fusion.Indexing;

/// <summary>
///     The cached per-file analysis the scoping pipeline needs: the types a file references, the types it
///     declares, and the symbols (types and members) it declares for relevance ranking.
/// </summary>
/// <param name="ReferencedTypes">The type names the file references, for forward dependency edges.</param>
/// <param name="DeclaredTypes">The type names the file declares, for reverse edges and the type index.</param>
/// <param name="DeclaredSymbols">The declared symbol names used to weight the relevance index.</param>
/// <remarks>
///     Keyed by content hash and analyzer tier, so a file is re-analyzed only when its content changes or the
///     analyzer (regex versus Roslyn) changes. This is what lets the persistent index amortize the cost of the
///     expensive Roslyn parse across the several calls an agent makes in one task.
/// </remarks>
public sealed record FileAnalysis(
    IReadOnlyList<string> ReferencedTypes,
    IReadOnlyList<string> DeclaredTypes,
    IReadOnlyList<string> DeclaredSymbols);
