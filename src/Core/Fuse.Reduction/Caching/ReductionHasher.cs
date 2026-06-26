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
    ///     Schema version for <see cref="HashReductionOptions" />. Bump when the hashed payload shape changes
    ///     so stale cache entries are not reused across incompatible analyzer tiers.
    /// </summary>
    internal const int SchemaVersion = 3;

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
    /// <remarks>
    ///     Only options that affect <c>ReduceContent</c> are included. Emission-only flags (route map, project
    ///     graph, pattern summary, redact report) are excluded so toggling them does not fragment the cache.
    /// </remarks>
    public static ulong HashReductionOptions(string extension, ReductionOptions options)
    {
        // Hash the single level plus the orthogonal flags. The per-transform C# decisions are derived from
        // Level, so hashing them too would be redundant and bloat the key.
        var payload = string.Join('|',
            SchemaVersion,
            extension,
            (int)options.Level,
            options.TrimContent,
            options.UseCondensing,
            options.MinifyXmlFiles,
            options.MinifyHtmlAndRazor,
            options.IncludeSemanticMarkers,
            options.EnableRedaction,
            options.CollapseGeneratedCode);

        var bytes = Encoding.UTF8.GetBytes(payload);
        return XxHash64.HashToUInt64(bytes);
    }
}
