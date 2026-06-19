namespace Fuse.Analysis.Skeleton;

/// <summary>
///     Result of skeleton extraction for a single file.
/// </summary>
public sealed record SkeletonResult(
    string FilePath,
    string SkeletonContent,
    int OriginalTokenCount,
    int SkeletonTokenCount);
