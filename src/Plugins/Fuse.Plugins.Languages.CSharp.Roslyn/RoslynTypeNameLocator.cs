using Fuse.Plugins.Abstractions.Dependencies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Plugins.Languages.CSharp.Roslyn;

/// <summary>
///     Roslyn-based <see cref="ITypeNameLocator" />. Locates declared types and members from the parsed syntax
///     tree, so partial classes and conditional compilation do not confuse declaration detection.
/// </summary>
public sealed class RoslynTypeNameLocator : ITypeNameLocator
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public bool ContainsTypeDefinition(string content, string typeName)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(typeName))
            return false;

        return EnumerateTypes(content).Any(t => string.Equals(t, typeName, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ExtractDefinedTypes(string content) =>
        string.IsNullOrWhiteSpace(content) ? [] : EnumerateTypes(content).Distinct(StringComparer.Ordinal).ToArray();

    /// <inheritdoc />
    public IReadOnlyList<string> ExtractDefinedSymbols(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var root = TryParse(content);
        if (root is null)
            return [];

        var symbols = new List<string>();
        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            symbols.Add(type.Identifier.ValueText);

        foreach (var member in root.DescendantNodes())
        {
            switch (member)
            {
                case MethodDeclarationSyntax m:
                    symbols.Add(m.Identifier.ValueText);
                    break;
                case PropertyDeclarationSyntax p:
                    symbols.Add(p.Identifier.ValueText);
                    break;
            }
        }

        return symbols;
    }

    private static IEnumerable<string> EnumerateTypes(string content)
    {
        var root = TryParse(content);
        if (root is null)
            return [];

        return root.DescendantNodes()
            .OfType<BaseTypeDeclarationSyntax>()
            .Select(t => t.Identifier.ValueText);
    }

    private static SyntaxNode? TryParse(string content)
    {
        try
        {
            return CSharpSyntaxTree.ParseText(content).GetRoot();
        }
        catch
        {
            return null;
        }
    }
}
