using System.IO.Hashing;
using System.Text;
using Fuse.Plugins.Abstractions.Options;

namespace Fuse.Reduction.Caching;

/// <summary>
///     Computes XXHash64 hashes for reduction cache keys.
/// </summary>
internal static class ReductionHasher
{
    /// <summary>
    ///     Hashes raw file content for cache lookup.
    /// </summary>
    public static ulong HashContent(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return XxHash64.HashToUInt64(bytes);
    }

    /// <summary>
    ///     Hashes file extension and reduction options for cache lookup.
    /// </summary>
    public static ulong HashReductionOptions(string extension, ReductionOptions options)
    {
        // Hash the single level plus the orthogonal flags. The per-transform C# decisions are derived from
        // Level, so hashing them too would be redundant and bloat the key.
        var payload = string.Join('|',
            extension,
            (int)options.Level,
            options.TrimContent,
            options.UseCondensing,
            options.MinifyXmlFiles,
            options.MinifyHtmlAndRazor,
            options.IncludeSemanticMarkers,
            options.IncludePatternSummary,
            options.EnableRedaction,
            options.IncludeRedactReport,
            options.IncludeRouteMap,
            options.IncludeProjectGraph,
            options.CollapseGeneratedCode);

        var bytes = Encoding.UTF8.GetBytes(payload);
        return XxHash64.HashToUInt64(bytes);
    }

    /// <summary>
    ///     Formats a cache file name from content and options hashes.
    /// </summary>
    public static string FormatCacheFileName(ulong contentHash, ulong reductionOptionsHash) =>
        $"{contentHash:x16}{reductionOptionsHash:x16}.txt";
}
