namespace Fuse.Plugins.Abstractions.Outline;

/// <summary>
///     Splits a source file into member-level <see cref="SymbolChunk" />s: one chunk per declared member, each
///     carrying its full text and line span. This is the capability behind symbol-level retrieval and packing
///     and behind near-duplicate body deduplication, both of which need member body spans the outline does not
///     expose.
/// </summary>
/// <remarks>
///     A precise (semantic) implementation registered later overrides a regex one by extension. The regex tier
///     produces coarser chunking but must still yield coherent, independently parseable member bodies so the
///     symbol-level path keeps working on the Native AOT build, where Roslyn is absent.
/// </remarks>
public interface ISymbolChunkExtractor : ILanguageCapability
{
    /// <summary>
    ///     Extracts the member-level chunks from the supplied content.
    /// </summary>
    /// <param name="content">
    ///     Source content to split. Must not be <see langword="null" />; an empty string yields an empty list.
    /// </param>
    /// <returns>
    ///     One <see cref="SymbolChunk" /> per declared member, in declaration order. Empty when the content
    ///     declares no members the extractor can resolve.
    /// </returns>
    IReadOnlyList<SymbolChunk> ExtractChunks(string content);
}
