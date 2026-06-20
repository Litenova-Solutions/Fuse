using System.Text.RegularExpressions;
using Fuse.Plugins.Abstractions.Dependencies;

namespace Fuse.Plugins.Languages.CSharp.Dependencies;

/// <summary>
///     Locates C# type definitions (<c>class</c>, <c>interface</c>, <c>record</c>, <c>struct</c>, and
///     <c>enum</c> declarations) in <c>.cs</c> content using regex matching.
/// </summary>
/// <remarks>
///     Matching is textual and not scope-aware. Comments and string literals are blanked before matching
///     (see <see cref="CSharpSourceSanitizer" />), so a keyword-plus-name shape in prose or text is not
///     reported as a definition; a generic constraint of the same shape still can be. Generic arity is
///     ignored: a type name is matched without its type parameters.
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

        content = CSharpSourceSanitizer.Sanitize(content);

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

        content = CSharpSourceSanitizer.Sanitize(content);

        var types = new List<string>();
        foreach (Match match in TypeDefinitionRegex().Matches(content))
            types.Add(match.Groups[2].Value);

        return types;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ExtractDefinedSymbols(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        content = CSharpSourceSanitizer.Sanitize(content);

        var symbols = new List<string>();
        foreach (Match match in TypeDefinitionRegex().Matches(content))
            symbols.Add(match.Groups[2].Value);

        foreach (Match match in MemberDefinitionRegex().Matches(content))
            symbols.Add(match.Groups[1].Value);

        return symbols;
    }

    [GeneratedRegex(@"\b(class|interface|record|struct|enum)\s+(\w+)\b", RegexOptions.Compiled)]
    private static partial Regex TypeDefinitionRegex();

    // Member declarations: an access or member modifier, a return type, then the member name and an opening
    // parenthesis (methods) or brace or arrow (properties). Best-effort, run on sanitized content.
    [GeneratedRegex(
        @"\b(?:public|internal|protected|private|static|async|virtual|override|sealed|abstract|extern)\s+(?:[\w<>\[\],\.\?]+\s+)+(\w+)\s*(?:\(|\{|=>)",
        RegexOptions.Compiled)]
    private static partial Regex MemberDefinitionRegex();
}
