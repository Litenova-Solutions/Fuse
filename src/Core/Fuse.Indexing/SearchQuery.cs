namespace Fuse.Indexing;

/// <summary>
///     A full-text search over indexed chunks.
/// </summary>
/// <param name="Text">The free-text query. Tokens are matched across the chunk path, name, symbols, signature, comments, and body.</param>
/// <param name="Limit">The maximum number of hits to return.</param>
public sealed record SearchQuery(string Text, int Limit = 50);

/// <summary>
///     A single full-text search hit, with the matching chunk and a normalized relevance score.
/// </summary>
/// <param name="ChunkId">The matching chunk id.</param>
/// <param name="FilePath">The path of the file the chunk belongs to.</param>
/// <param name="Kind">The chunk kind.</param>
/// <param name="Name">The chunk name, when set.</param>
/// <param name="StartLine">The 1-based start line of the chunk.</param>
/// <param name="EndLine">The 1-based end line of the chunk.</param>
/// <param name="Score">The relevance score, normalized so higher is better.</param>
public sealed record SearchHit(
    string ChunkId,
    string FilePath,
    string Kind,
    string? Name,
    int StartLine,
    int EndLine,
    double Score);
