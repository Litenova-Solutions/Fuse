namespace Fuse.Reduction.Skeleton;

/// <summary>
///     Resolves skeleton extractors by file extension.
/// </summary>
public sealed class SkeletonExtractorRegistry
{
    private readonly Dictionary<string, ISkeletonExtractor> _extractors;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SkeletonExtractorRegistry" /> class.
    /// </summary>
    /// <param name="extractors">The registered skeleton extractors.</param>
    public SkeletonExtractorRegistry(IEnumerable<ISkeletonExtractor> extractors)
    {
        _extractors = extractors.ToDictionary(
            extractor => extractor.Extension,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Attempts to resolve a skeleton extractor for the specified file extension.
    /// </summary>
    /// <param name="extension">The file extension, including the leading dot.</param>
    /// <returns>The matching extractor, or <c>null</c> when none is registered.</returns>
    public ISkeletonExtractor? TryGetExtractor(string extension) =>
        _extractors.GetValueOrDefault(extension);
}
