namespace Fuse.Changes.Model;

/// <summary>The declaration changes of one file between HEAD and the working tree.</summary>
/// <param name="Changes">
///     In the order HEAD declares them, then the declarations only the working tree has, in its order. Empty when
///     only bodies, initializers of non-constant fields, formatting or comments changed.
/// </param>
/// <param name="HasBroadChange">
///     True when one change can break code that never names the declaration: a changed type header (its attributes,
///     modifiers, kind, type parameters, primary constructor, constraints or base types), or any change to a delegate, a
///     global using or a file-level attribute list. No reference search can bound what such a change breaks.
/// </param>
internal sealed record FileChanges(IReadOnlyList<DeclarationChange> Changes, bool HasBroadChange);
