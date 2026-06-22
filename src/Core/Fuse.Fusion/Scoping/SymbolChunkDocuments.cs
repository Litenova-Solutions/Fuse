using Fuse.Collection.Models;
using Fuse.Plugins.Abstractions.Outline;

namespace Fuse.Fusion.Scoping;

/// <summary>
///     A reference back from a relevance-index document key to the member chunk it was built from, so a ranked
///     chunk can be attributed to its file and member.
/// </summary>
/// <param name="FilePath">The normalized relative path of the file the chunk belongs to.</param>
/// <param name="QualifiedName">
///     The <c>ParentType.Member</c> qualified name, unique within the file, or <see langword="null" /> for the
///     per-file type-header document that carries the declared type names for file discovery and selects no
///     member.
/// </param>
internal sealed record ChunkRef(string FilePath, string? QualifiedName);

/// <summary>
///     Builds relevance-index documents at member (chunk) granularity. A file with a registered chunk extractor
///     contributes one document per declared member, keyed by a synthetic chunk id; every other file
///     contributes a single whole-file document keyed by its path, so the file-granular path is preserved for
///     languages without a chunker.
/// </summary>
internal static class SymbolChunkDocuments
{
    // The chunk id is only ever used as a dictionary key and is never parsed back (a ChunkRef is stored
    // alongside it), so the separator just has to keep ids for distinct members of one file unique.
    private const char Separator = '#';

    /// <summary>
    ///     Adds the documents for a single file to <paramref name="documents" />, recording a
    ///     <see cref="ChunkRef" /> for each member chunk in <paramref name="chunkRefs" />.
    /// </summary>
    public static void AddFile(
        Dictionary<string, IndexedDocument> documents,
        Dictionary<string, ChunkRef> chunkRefs,
        SourceFile file,
        string content,
        IReadOnlyList<string>? declaredSymbols,
        ISymbolChunkExtractor? chunkExtractor)
    {
        var path = file.NormalizedRelativePath;
        var chunks = chunkExtractor?.ExtractChunks(content);
        if (chunks is null || chunks.Count == 0)
        {
            documents[path] = new IndexedDocument(content, path, declaredSymbols);
            return;
        }

        foreach (var chunk in chunks)
        {
            var id = $"{path}{Separator}{chunk.QualifiedName}{Separator}{chunk.StartLine}";

            // The symbol field carries only the member's own name, not its parent type, so members of one type
            // are distinguished from each other: a query naming a method lands that method's chunk and not its
            // siblings, which share the type name.
            documents[id] = new IndexedDocument(chunk.Content, path, [chunk.SymbolName]);
            chunkRefs[id] = new ChunkRef(path, chunk.QualifiedName);
        }

        // A per-file type-header document carries the declared type names so a query by type name (or a path
        // match) still discovers the file. It has an empty body, so it adds no body-field noise, and it selects
        // no member, so matching it alone keeps the whole file rather than collapsing it to one member.
        if (declaredSymbols is { Count: > 0 })
        {
            var headerId = $"{path}{Separator}__type__";
            documents[headerId] = new IndexedDocument(string.Empty, path, declaredSymbols);
            chunkRefs[headerId] = new ChunkRef(path, QualifiedName: null);
        }
    }
}
