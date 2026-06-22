using System.Text;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Outline;
using Fuse.Reduction.Models;
using Fuse.Reduction.Tokenization;

namespace Fuse.Fusion.Enrichment;

/// <summary>
///     Detects member bodies that are identical after whitespace normalization across two or more files (for
///     example generated or templated members beyond what <c>GeneratedCodeCollapser</c> handles) and replaces
///     each non-canonical occurrence with a marker that references the canonical instance, keeping the member
///     signature intact.
/// </summary>
/// <remarks>
///     This extends header deduplication (see <see cref="BoilerplateDeduplicator" />) from leading comment
///     blocks to member bodies, reusing the chunk model from symbol-level retrieval to locate member spans.
///     The match is conservative: bodies must be byte-identical after collapsing whitespace, not merely
///     similar, and only method and constructor bodies are considered. The signature and opening brace are
///     always preserved, so no public API surface is dropped; only the body statements are replaced by a
///     reference. The first occurrence in entry order is the canonical one and is emitted in full.
/// </remarks>
public sealed class BodyDeduplicator
{
    // Bodies shorter than this (normalized) are not worth a marker; small accessors and one-liners stay inline.
    private const int MinimumBodyLength = 48;
    private const int MinimumSharedFiles = 2;

    private readonly CapabilityRegistry<ISymbolChunkExtractor> _chunkExtractors;

    /// <summary>
    ///     Initializes a new instance of the <see cref="BodyDeduplicator" /> class.
    /// </summary>
    /// <param name="chunkExtractors">Registry of per-extension member chunk extractors used to locate bodies.</param>
    public BodyDeduplicator(CapabilityRegistry<ISymbolChunkExtractor> chunkExtractors)
    {
        _chunkExtractors = chunkExtractors;
    }

    /// <summary>
    ///     Deduplicates near-identical member bodies across the supplied entries.
    /// </summary>
    /// <param name="content">The reduced entries to scan and rewrite.</param>
    /// <param name="tokenCounter">The token counter used to recompute the token count of rewritten entries.</param>
    /// <returns>The rewritten entries and counts of bodies deduplicated and members rewritten.</returns>
    public BodyDeduplicationResult Deduplicate(IReadOnlyList<FusedContent> content, ITokenCounter tokenCounter)
    {
        // Pass 1: index every qualifying member body by its normalized form, recording which files contain it.
        var occurrences = new Dictionary<string, BodyGroup>(StringComparer.Ordinal);
        var perEntryChunks = new IReadOnlyList<SymbolChunk>?[content.Count];

        for (var i = 0; i < content.Count; i++)
        {
            var extractor = _chunkExtractors.TryResolve(content[i].SourceFile.Extension);
            if (extractor is null)
                continue;

            var chunks = extractor.ExtractChunks(content[i].Content);
            perEntryChunks[i] = chunks;

            foreach (var chunk in chunks)
            {
                var normalized = NormalizeBody(chunk);
                if (normalized is null)
                    continue;

                if (!occurrences.TryGetValue(normalized, out var group))
                {
                    group = new BodyGroup(i, chunk);
                    occurrences[normalized] = group;
                }

                group.Files.Add(content[i].NormalizedPath);
                group.Hits.Add((i, chunk.QualifiedName));
            }
        }

        // Assign a marker id to each body shared across at least two distinct files.
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (normalized, group) in occurrences.OrderBy(kv => kv.Value.CanonicalEntry))
        {
            if (group.Files.Count >= MinimumSharedFiles)
                ids[normalized] = ids.Count + 1;
        }

        if (ids.Count == 0)
            return new BodyDeduplicationResult(content, 0, 0);

        // Map each entry to the set of member bodies (by qualified name) it must rewrite, plus the marker text.
        var rewritesByEntry = new Dictionary<int, Dictionary<string, string>>();
        var membersRewritten = 0;
        foreach (var (normalized, group) in occurrences)
        {
            if (!ids.TryGetValue(normalized, out var id))
                continue;

            var canonicalPath = content[group.CanonicalEntry].NormalizedPath;
            var canonicalName = group.CanonicalChunk.QualifiedName;

            foreach (var (entryIndex, qualified) in group.Hits)
            {
                // Keep the canonical occurrence in full; rewrite every other occurrence.
                if (entryIndex == group.CanonicalEntry && qualified == canonicalName)
                    continue;

                if (!rewritesByEntry.TryGetValue(entryIndex, out var map))
                {
                    map = new Dictionary<string, string>(StringComparer.Ordinal);
                    rewritesByEntry[entryIndex] = map;
                }

                map[qualified] = $"// fuse:body[{id}] identical to {canonicalName} in {canonicalPath}";
                membersRewritten++;
            }
        }

        // Pass 2: rewrite the affected entries, replacing each duplicate body with its marker.
        var rewritten = new FusedContent[content.Count];
        for (var i = 0; i < content.Count; i++)
        {
            if (!rewritesByEntry.TryGetValue(i, out var markers) || perEntryChunks[i] is not { } chunks)
            {
                rewritten[i] = content[i];
                continue;
            }

            var body = RewriteBodies(content[i].Content, chunks, markers);
            rewritten[i] = content[i].WithReducedContent(body, tokenCounter);
        }

        return new BodyDeduplicationResult(rewritten, ids.Count, membersRewritten);
    }

    // The normalized form of a method or constructor body (whitespace collapsed), or null when the chunk has no
    // brace body or is too short to be worth deduplicating.
    private static string? NormalizeBody(SymbolChunk chunk)
    {
        if (chunk.SymbolKind is not ("method" or "constructor"))
            return null;

        var open = chunk.Content.IndexOf('{');
        var close = chunk.Content.LastIndexOf('}');
        if (open < 0 || close <= open)
            return null;

        var inner = chunk.Content[(open + 1)..close];
        var normalized = CollapseWhitespace(inner);
        return normalized.Length < MinimumBodyLength ? null : normalized;
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    // Walks the content line by line, replacing each member named in markers with its signature, opening brace,
    // the marker, and a closing brace. Lines outside a rewritten member pass through unchanged.
    private static string RewriteBodies(
        string content,
        IReadOnlyList<SymbolChunk> chunks,
        IReadOnlyDictionary<string, string> markers)
    {
        var lines = content.Split('\n');
        var ordered = chunks.OrderBy(c => c.StartLine).ToList();
        var sb = new StringBuilder(content.Length);
        var cursor = 1; // 1-based next line to emit.

        foreach (var chunk in ordered)
        {
            if (chunk.StartLine < cursor || chunk.StartLine > lines.Length)
                continue;

            for (var l = cursor; l < chunk.StartLine; l++)
                sb.Append(lines[l - 1]).Append('\n');

            if (markers.TryGetValue(chunk.QualifiedName, out var marker))
            {
                sb.Append(CollapseToMarker(lines, chunk, marker)).Append('\n');
            }
            else
            {
                var end = Math.Min(chunk.EndLine, lines.Length);
                for (var l = chunk.StartLine; l <= end; l++)
                    sb.Append(lines[l - 1]).Append('\n');
            }

            cursor = chunk.EndLine + 1;
        }

        for (var l = cursor; l <= lines.Length; l++)
            sb.Append(lines[l - 1]).Append('\n');

        return sb.ToString().TrimEnd('\n');
    }

    // Keeps everything up to and including the member's opening brace, then the marker, then a closing brace,
    // so the signature survives and only the body statements are replaced.
    private static string CollapseToMarker(string[] lines, SymbolChunk chunk, string marker)
    {
        var indent = Indent(lines[chunk.StartLine - 1]);
        var end = Math.Min(chunk.EndLine, lines.Length);
        var member = string.Join('\n', lines[(chunk.StartLine - 1)..end]);

        var open = member.IndexOf('{');
        var head = open < 0 ? member.TrimEnd() : member[..(open + 1)].TrimEnd();

        return $"{head}\n{indent}    {marker}\n{indent}}}";
    }

    private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];

    private sealed class BodyGroup(int canonicalEntry, SymbolChunk canonicalChunk)
    {
        public int CanonicalEntry { get; } = canonicalEntry;
        public SymbolChunk CanonicalChunk { get; } = canonicalChunk;
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(int EntryIndex, string Qualified)> Hits { get; } = [];
    }
}
