namespace Fuse.Indexing;

/// <summary>
///     A full-text search over indexed chunks.
/// </summary>
/// <param name="Text">The free-text query. Tokens are matched across the chunk path, name, symbols, signature, comments, and body.</param>
/// <param name="Limit">The maximum number of hits to return.</param>
public sealed record SearchQuery(string Text, int Limit = 50);

/// <summary>
///     A single full-text search hit, with the matching chunk and a normalized relevance score.
/// </summary>
/// <param name="ChunkId">The matching chunk id.</param>
/// <param name="FilePath">The path of the file the chunk belongs to.</param>
/// <param name="Kind">The chunk kind.</param>
/// <param name="Name">The chunk name, when set.</param>
/// <param name="StartLine">The 1-based start line of the chunk.</param>
/// <param name="EndLine">The 1-based end line of the chunk.</param>
/// <param name="Score">The relevance score, normalized so higher is better.</param>
public sealed record SearchHit(
    string ChunkId,
    string FilePath,
    string Kind,
    string? Name,
    int StartLine,
    int EndLine,
    double Score);

/// <summary>
///     A summary of an indexed symbol, returned by listing queries (for example the workspace map).
/// </summary>
/// <param name="SymbolId">The symbol id.</param>
/// <param name="Name">The simple name.</param>
/// <param name="Kind">The symbol kind.</param>
/// <param name="FullyQualifiedName">The fully qualified name.</param>
/// <param name="FilePath">The declaring file's normalized path.</param>
/// <param name="StartLine">The 1-based start line.</param>
/// <param name="IsPublicApi">Whether the symbol is part of the public API surface.</param>
public sealed record SymbolListItem(
    string SymbolId,
    string Name,
    string Kind,
    string FullyQualifiedName,
    string FilePath,
    int StartLine,
    bool IsPublicApi);

/// <summary>
///     A summary of an indexed route, returned by listing queries (for example the workspace map).
/// </summary>
/// <param name="RouteId">The route id.</param>
/// <param name="HttpMethod">The HTTP method.</param>
/// <param name="RoutePattern">The route pattern.</param>
/// <param name="FilePath">The declaring file's normalized path.</param>
/// <param name="StartLine">The 1-based start line.</param>
public sealed record RouteListItem(
    string RouteId,
    string HttpMethod,
    string RoutePattern,
    string FilePath,
    int StartLine);
