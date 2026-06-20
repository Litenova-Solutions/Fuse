namespace Fuse.Fusion.Scoping;

/// <summary>
///     Options for BM25 query-scoped file selection.
/// </summary>
/// <param name="Query">The natural-language or keyword query.</param>
/// <param name="TopFiles">Maximum seed files to select before dependency expansion.</param>
/// <param name="Depth">Dependency traversal depth after seed selection.</param>
/// <param name="Rerank">
///     Whether to rerank the BM25 candidates with embedding-vector similarity (hybrid retrieval) before
///     dependency expansion. Off by default.
/// </param>
public sealed record QueryOptions(string Query, int TopFiles = 10, int Depth = 1, bool Rerank = false);
