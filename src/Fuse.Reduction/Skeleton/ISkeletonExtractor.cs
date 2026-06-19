namespace Fuse.Reduction.Skeleton;

/// <summary>
///     Extracts a structural skeleton from reduced source content.
/// </summary>
public interface ISkeletonExtractor
{
    /// <summary>
    ///     Gets the file extension this extractor handles, including the leading dot.
    /// </summary>
    string Extension { get; }

    /// <summary>
    ///     Extracts structural signatures from the supplied content.
    /// </summary>
    /// <param name="content">Already-reduced source content.</param>
    /// <returns>Skeleton content with method bodies suppressed.</returns>
    string ExtractSkeleton(string content);
}
