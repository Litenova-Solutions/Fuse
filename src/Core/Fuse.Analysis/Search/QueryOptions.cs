namespace Fuse.Analysis.Search;

/// <summary>
///     Options for BM25 query-scoped file selection.
/// </summary>
/// <param name="Query">The natural-language or keyword query.</param>
/// <param name="TopFiles">Maximum seed files to select before dependency expansion.</param>
/// <param name="Depth">Dependency traversal depth after seed selection.</param>
public sealed record QueryOptions(string Query, int TopFiles = 10, int Depth = 1);
