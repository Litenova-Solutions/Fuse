namespace Fuse.Languages.Abstractions.Skeleton;

/// <summary>
///     Extracts a structural skeleton from reduced source content.
/// </summary>
public interface ISkeletonExtractor : ILanguageCapability
{
    /// <summary>
    ///     Extracts structural signatures from the supplied content.
    /// </summary>
    /// <param name="content">Already-reduced source content.</param>
    /// <param name="publicApiOnly">When <c>true</c>, emits only public and protected members.</param>
    /// <returns>Skeleton content with method bodies suppressed.</returns>
    string ExtractSkeleton(string content, bool publicApiOnly = false);
}
