namespace Fuse.Plugins.Abstractions.Dependencies;

/// <summary>
///     Locates type definitions within source content for a specific language, mapping declared types to
///     the file that declares them.
/// </summary>
/// <remarks>
///     Resolved by file extension through <see cref="CapabilityRegistry{TCapability}" /> using
///     <see cref="ILanguageCapability.SupportedExtensions" />. Used alongside <see cref="IDependencyExtractor" />
///     to index which file owns each type when building a dependency graph. Implementations must be stateless
///     to allow concurrent scanning across files.
/// </remarks>
public interface ITypeNameLocator : ILanguageCapability
{
    /// <summary>
    ///     Determines whether the content declares a type with the given name.
    /// </summary>
    /// <param name="content">Source content to scan. Must not be <see langword="null" />.</param>
    /// <param name="typeName">Simple name of the type to look for.</param>
    /// <returns>
    ///     <see langword="true" /> when the content declares a type named <paramref name="typeName" />;
    ///     otherwise <see langword="false" />.
    /// </returns>
    bool ContainsTypeDefinition(string content, string typeName);

    /// <summary>
    ///     Extracts the names of all types defined in the content for dependency-graph indexing.
    /// </summary>
    /// <param name="content">
    ///     Source content to scan. Must not be <see langword="null" />; an empty string yields an empty list.
    /// </param>
    /// <returns>The simple names of every type declared in the content. Empty when none are found.</returns>
    IReadOnlyList<string> ExtractDefinedTypes(string content);

    /// <summary>
    ///     Extracts declared symbol names (types and, where the language allows, members such as methods and
    ///     properties) for relevance ranking. The default returns only the declared types.
    /// </summary>
    /// <param name="content">
    ///     Source content to scan. Must not be <see langword="null" />; an empty string yields an empty list.
    /// </param>
    /// <returns>
    ///     The declared symbol names used to populate the high-weight symbol field of the relevance index.
    /// </returns>
    IReadOnlyList<string> ExtractDefinedSymbols(string content) => ExtractDefinedTypes(content);
}
