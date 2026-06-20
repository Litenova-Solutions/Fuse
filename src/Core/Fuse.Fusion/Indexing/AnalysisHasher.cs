using System.IO.Hashing;
using System.Text;

namespace Fuse.Fusion.Indexing;

/// <summary>
///     Derives the persistent-index key from a file's content and the analyzer tier that produced its analysis.
/// </summary>
public static class AnalysisHasher
{
    /// <summary>
    ///     Computes a stable hex key from content and an analyzer tier tag.
    /// </summary>
    /// <param name="content">The file content.</param>
    /// <param name="tierTag">
    ///     A tag identifying the analyzer that produced the analysis (for example the extractor type names), so
    ///     a regex entry and a Roslyn entry for the same content do not collide.
    /// </param>
    /// <returns>A 32-character lowercase hex key combining both hashes.</returns>
    public static string Key(string content, string tierTag)
    {
        var contentHash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(content));
        var tierHash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(tierTag));
        return $"{contentHash:x16}{tierHash:x16}";
    }
}
