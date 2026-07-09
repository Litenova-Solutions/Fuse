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
///     An exact signature lookup result for a symbol, returned by the batch signature query behind
///     <c>fuse_find</c> (kind=signatures): the compiler-shaped answer to "what is the exact shape of this member".
/// </summary>
/// <param name="Name">The simple symbol name.</param>
/// <param name="Kind">The symbol kind (type, method, property, and so on).</param>
/// <param name="FullyQualifiedName">The fully qualified name.</param>
/// <param name="Signature">The declared signature, when the semantic tier recorded one; null in syntax mode.</param>
/// <param name="Accessibility">The declared accessibility (public, internal, and so on), when known.</param>
/// <param name="ContainingType">The declaring type's name, when the symbol is a member.</param>
/// <param name="FilePath">The declaring file's normalized path.</param>
/// <param name="StartLine">The 1-based declaration line.</param>
/// <param name="IsPublicApi">Whether the symbol is part of the public API surface.</param>
public sealed record SymbolSignature(
    string Name,
    string Kind,
    string FullyQualifiedName,
    string? Signature,
    string? Accessibility,
    string? ContainingType,
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

/// <summary>
///     A summary of an indexed file, returned by path lookups.
/// </summary>
/// <param name="Path">The file path as discovered.</param>
/// <param name="NormalizedPath">The normalized (forward-slash) path.</param>
/// <param name="Extension">The file extension.</param>
/// <param name="IsTest">Whether the file is a test file.</param>
/// <param name="IsGenerated">Whether the file is generated.</param>
public sealed record FileListItem(
    string Path,
    string NormalizedPath,
    string Extension,
    bool IsTest,
    bool IsGenerated);
