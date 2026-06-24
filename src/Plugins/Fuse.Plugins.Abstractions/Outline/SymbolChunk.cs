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
/// <param name="StableId">
///     A collision-free identity for the member: namespace, the full containing-type chain (with generic
///     arity), the member name, and for methods and constructors the generic arity and parameter type list.
///     Unlike <see cref="QualifiedName" /> it distinguishes overloads, nested types that share a simple parent
///     name, the same member name across namespaces, and members of partial classes, so member operations
///     (selection, thin-skeleton assembly, body deduplication, slicing) key on it rather than on the
///     display name. When an extractor cannot compute the components it falls back to <see cref="QualifiedName" />.
/// </param>
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
    int EndLine,
    string? StableId = null)
{
    /// <summary>
    ///     The fully qualified member name (<c>ParentType.SymbolName</c>), or the simple name when the member
    ///     has no parent type. Intended for display (provenance, markers); use <see cref="Identity" /> to key
    ///     member operations, since <see cref="QualifiedName" /> collides for overloads and like-named members.
    /// </summary>
    public string QualifiedName => ParentType is null ? SymbolName : $"{ParentType}.{SymbolName}";

    /// <summary>
    ///     The collision-free identity used to key member operations: <see cref="StableId" /> when the extractor
    ///     computed one, otherwise <see cref="QualifiedName" />.
    /// </summary>
    public string Identity => StableId ?? QualifiedName;
}
