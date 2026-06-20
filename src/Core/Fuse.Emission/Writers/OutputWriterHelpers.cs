using Fuse.Emission.Models;
using Fuse.Reduction.Models;

namespace Fuse.Emission.Writers;

/// <summary>
///     Shared helpers for output writers.
/// </summary>
internal static class OutputWriterHelpers
{
    internal static IReadOnlyList<FileTokenInfo> BuildTopTokenFiles(
        IReadOnlyList<FileTokenInfo>? stats,
        int take = 5)
    {
        if (stats is null)
            return Array.Empty<FileTokenInfo>();

        return stats.OrderByDescending(f => f.Count).Take(take).ToList();
    }
}
