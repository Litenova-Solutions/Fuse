using System.Text.RegularExpressions;
using Fuse.Languages.Abstractions.Dependencies;

namespace Fuse.Languages.CSharp.Dependencies;

/// <summary>
///     Locates C# type definitions using regex matching.
/// </summary>
public sealed partial class CSharpTypeNameLocator : ITypeNameLocator
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public bool ContainsTypeDefinition(string content, string typeName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(typeName))
            return false;

        foreach (Match match in TypeDefinitionRegex().Matches(content))
        {
            if (string.Equals(match.Groups[2].Value, typeName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ExtractDefinedTypes(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var types = new List<string>();
        foreach (Match match in TypeDefinitionRegex().Matches(content))
            types.Add(match.Groups[2].Value);

        return types;
    }

    [GeneratedRegex(@"\b(class|interface|record|struct|enum)\s+(\w+)\b", RegexOptions.Compiled)]
    private static partial Regex TypeDefinitionRegex();
}
