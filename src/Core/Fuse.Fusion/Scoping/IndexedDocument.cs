namespace Fuse.Fusion.Scoping;

/// <summary>
///     A fielded document for relevance indexing. Fields are weighted independently so that matches on a
///     file's declared symbols or its path count for more than matches buried in its body.
/// </summary>
/// <param name="Content">The file body. The base field, weighted lowest.</param>
/// <param name="Path">
///     The normalized relative path, or <c>null</c> to omit the path field. Tokenized into path and filename
///     sub-words.
/// </param>
/// <param name="Symbols">
///     The declared type and member names, or <c>null</c> to omit the symbol field. Weighted highest.
/// </param>
public sealed record IndexedDocument(
    string Content,
    string? Path = null,
    IReadOnlyList<string>? Symbols = null);
