using Fuse.Plugins.Abstractions.Outline;

namespace Fuse.Emission.Manifest;

/// <summary>
///     One file in a table of contents: its path, the token cost of reading it under the current reduction,
///     and its structural outline.
/// </summary>
/// <param name="Path">The normalized, forward-slash relative path of the file.</param>
/// <param name="Tokens">The token cost of reading the file's reduced content.</param>
/// <param name="Symbols">The declared types and members, or an empty list when no outline is available.</param>
public sealed record TableOfContentsFileEntry(string Path, long Tokens, IReadOnlyList<OutlineSymbol> Symbols);
