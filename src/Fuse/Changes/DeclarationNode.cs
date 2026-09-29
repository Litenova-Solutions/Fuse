using Fuse.Changes.Model;
using Microsoft.CodeAnalysis;

namespace Fuse.Changes;

/// <summary>One declaration in one version of a file, with the syntax that declares it and what other files can see of it.</summary>
/// <param name="Key">Its identity within the file.</param>
/// <param name="Node">
///     The syntax that declares it, from which the declared symbol resolves: the variable declarator for a field or an
///     event field, the compilation unit for top-level statements, and the declaration itself otherwise.
/// </param>
/// <param name="Surface">
///     Everything about the declaration that other files can observe, with bodies and trivia removed, or null when other
///     files observe nothing of it (a finalizer, top-level statements). For a type it is the header alone, so a changed
///     member leaves its type's surface as it was.
/// </param>
/// <param name="Names">
///     Identifiers other code would use to reach it: the member name and its containing type's name, or for an ordinary
///     using directive, the names of the types the file declares.
/// </param>
/// <param name="Container">The type that declares it, or null for a declaration at namespace or file level.</param>
internal sealed record DeclarationNode(DeclarationKey Key, SyntaxNode Node, string? Surface, IReadOnlyList<string> Names, DeclarationKey.NamedType? Container);
