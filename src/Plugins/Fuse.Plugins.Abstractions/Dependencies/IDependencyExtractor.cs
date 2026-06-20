namespace Fuse.Plugins.Abstractions.Dependencies;

/// <summary>
///     Extracts referenced type names from source content for dependency-graph construction.
/// </summary>
/// <remarks>
///     Produces a best-effort approximation: it may miss dynamically dispatched dependencies and may
///     produce false positives from type names appearing in comments or strings. Resolved by file extension
///     through <see cref="CapabilityRegistry{TCapability}" /> using
///     <see cref="ILanguageCapability.SupportedExtensions" />. Implementations must be stateless to allow
///     concurrent extraction across files.
/// </remarks>
public interface IDependencyExtractor : ILanguageCapability
{
    /// <summary>
    ///     Extracts the type names referenced by the content.
    /// </summary>
    /// <param name="content">
    ///     Source content to scan. Must not be <see langword="null" />; an empty string yields an empty list.
    /// </param>
    /// <returns>
    ///     The distinct referenced type names found, as a best-effort approximation. Empty when no references
    ///     are detected.
    /// </returns>
    IReadOnlyList<string> ExtractReferencedTypes(string content);
}
