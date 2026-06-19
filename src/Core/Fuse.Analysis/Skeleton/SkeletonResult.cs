namespace Fuse.Analysis.Skeleton;

/// <summary>
///     Result of skeleton extraction for a single file.
/// </summary>
/// <param name="FilePath">Normalized relative path of the file the skeleton was extracted from.</param>
/// <param name="SkeletonContent">The reduced skeleton content, retaining declarations while dropping bodies.</param>
/// <param name="OriginalTokenCount">Token count of the file before skeleton extraction.</param>
/// <param name="SkeletonTokenCount">Token count of <paramref name="SkeletonContent" />.</param>
public sealed record SkeletonResult(
    string FilePath,
    string SkeletonContent,
    int OriginalTokenCount,
    int SkeletonTokenCount);
