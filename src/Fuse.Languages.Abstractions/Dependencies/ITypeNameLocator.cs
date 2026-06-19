namespace Fuse.Languages.Abstractions.Dependencies;

/// <summary>
///     Locates type definitions within source content for a specific language.
/// </summary>
public interface ITypeNameLocator : ILanguageCapability
{
    /// <summary>Returns true when content defines a type with the given name.</summary>
    bool ContainsTypeDefinition(string content, string typeName);

    /// <summary>Extracts all type names defined in content (for dependency graph indexing).</summary>
    IReadOnlyList<string> ExtractDefinedTypes(string content);
}
