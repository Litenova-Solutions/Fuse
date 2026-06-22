namespace Fuse.Plugins.Abstractions.Outline;

/// <summary>
///     A single member-level slice of a source file: one declared member together with its full text and the
///     line range it occupies. Chunks are the unit of symbol-level retrieval and packing, where selection moves
///     from whole files to the individual members a task needs.
/// </summary>
/// <param name="SymbolKind">
///     The member kind, such as <c>method</c>, <c>property</c>, <c>constructor</c>, <c>field</c>, <c>event</c>,
///     or <c>enum-member</c>. Languages that do not distinguish kinds may use a single label.
/// </param>
/// <param name="SymbolName">The simple (unqualified) name of the member.</param>
/// <param name="ParentType">
///     The simple name of the type that declares the member, or <see langword="null" /> when the member is not
///     nested in a type.
/// </param>
/// <param name="Content">
///     The member's source text, including its signature and body, as a standalone fragment that parses on its
///     own. Used both as the indexed body and as the text inlined into a thin host skeleton.
/// </param>
/// <param name="StartLine">The 1-based line at which the member begins, inclusive.</param>
/// <param name="EndLine">The 1-based line at which the member ends, inclusive.</param>
/// <remarks>
///     A precise (semantic) extractor resolves member boundaries from a parse tree; a regex extractor produces
///     coarser but coherent boundaries by brace-depth scanning and may merge members it cannot separate. Both
///     guarantee that <see cref="Content" /> for a method or property body is independently parseable, which the
///     body-integrity check relies on.
/// </remarks>
public sealed record SymbolChunk(
    string SymbolKind,
    string SymbolName,
    string? ParentType,
    string Content,
    int StartLine,
    int EndLine)
{
    /// <summary>
    ///     The fully qualified member name (<c>ParentType.SymbolName</c>), or the simple name when the member
    ///     has no parent type. Used as a stable chunk identifier within a file.
    /// </summary>
    public string QualifiedName => ParentType is null ? SymbolName : $"{ParentType}.{SymbolName}";
}
