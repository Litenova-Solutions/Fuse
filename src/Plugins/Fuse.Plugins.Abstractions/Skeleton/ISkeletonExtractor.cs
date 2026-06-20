namespace Fuse.Plugins.Abstractions.Skeleton;

/// <summary>
///     Extracts a structural skeleton from reduced source content, retaining type and member signatures
///     while suppressing method bodies.
/// </summary>
/// <remarks>
///     Resolved by file extension through <see cref="CapabilityRegistry{TCapability}" /> using
///     <see cref="ILanguageCapability.SupportedExtensions" />. Implementations must be stateless to allow
///     concurrent extraction across files.
/// </remarks>
public interface ISkeletonExtractor : ILanguageCapability
{
    /// <summary>
    ///     Extracts structural signatures from the supplied content, dropping implementation bodies.
    /// </summary>
    /// <param name="content">
    ///     Already-reduced source content. Must not be <see langword="null" />; an empty string yields an
    ///     empty result.
    /// </param>
    /// <param name="publicApiOnly">
    ///     When <see langword="true" />, emits only public and protected members; when <see langword="false" />,
    ///     emits members of all accessibility levels.
    /// </param>
    /// <returns>
    ///     Skeleton content with method bodies suppressed. Returns content with no extractable structure unchanged.
    /// </returns>
    string ExtractSkeleton(string content, bool publicApiOnly = false);
}
