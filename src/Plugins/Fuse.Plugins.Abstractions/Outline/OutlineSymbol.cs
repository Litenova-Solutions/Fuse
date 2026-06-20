namespace Fuse.Plugins.Abstractions.Outline;

/// <summary>
///     A single declared symbol in a source file's structural outline: a type together with the names of the
///     members it declares.
/// </summary>
/// <param name="Kind">
///     The declaration keyword for the symbol, such as <c>class</c>, <c>interface</c>, <c>record</c>,
///     <c>struct</c>, or <c>enum</c>. Languages that do not distinguish kinds may use a single label.
/// </param>
/// <param name="Name">The simple (unqualified) name of the type.</param>
/// <param name="Members">
///     The simple names of members declared directly on the type (methods, properties, enum members), in
///     declaration order. Empty when the type declares no members or the extractor cannot resolve them.
/// </param>
/// <remarks>
///     An outline is a navigation aid, not a precise API surface: a best-effort extractor may omit members it
///     cannot resolve and a precise extractor may include accessibility the outline does not record. Consumers
///     should treat the member list as indicative rather than authoritative.
/// </remarks>
public sealed record OutlineSymbol(string Kind, string Name, IReadOnlyList<string> Members);
