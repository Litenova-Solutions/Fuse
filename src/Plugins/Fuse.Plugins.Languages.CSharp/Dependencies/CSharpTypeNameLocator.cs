using System.Text.RegularExpressions;
using Fuse.Plugins.Abstractions.Dependencies;

namespace Fuse.Plugins.Languages.CSharp.Dependencies;

/// <summary>
///     Locates C# type definitions (<c>class</c>, <c>interface</c>, <c>record</c>, <c>struct</c>, and
///     <c>enum</c> declarations) in <c>.cs</c> content using regex matching.
/// </summary>
/// <remarks>
///     Matching is textual and not scope-aware, so the same keyword-plus-name shape appearing in a comment,
///     string literal, or generic constraint can be reported as a definition. Generic arity is ignored:
///     a type name is matched without its type parameters.
/// </remarks>
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
