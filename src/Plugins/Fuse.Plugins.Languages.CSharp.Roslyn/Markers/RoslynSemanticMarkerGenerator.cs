using Fuse.Plugins.Abstractions.Markers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Plugins.Languages.CSharp.Roslyn.Markers;

/// <summary>
///     Generates one <see cref="SemanticMarker" /> per type declaration in <c>.cs</c> content using Roslyn
///     syntax analysis, capturing the type kind, implemented interfaces, constructor parameter types, and
///     inferred dependencies.
/// </summary>
/// <remarks>
///     The parser is error-tolerant, so skeleton and aggressively reduced content that is no longer
///     well-formed C# still yields markers from the partial tree. Record primary-constructor parameters are
///     not treated as constructor dependencies; that matches the prior regex generator.
/// </remarks>
public sealed class RoslynSemanticMarkerGenerator : ISemanticMarkerGenerator
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public IReadOnlyList<SemanticMarker> GenerateMarkers(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var root = TryParse(content);
        if (root is null)
            return [];

        var markers = new List<SemanticMarker>();
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var kind = KindOf(type);
            var constructorTypes = ConstructorParameterTypes(type);
            markers.Add(new SemanticMarker(
                type.Identifier.ValueText,
                kind,
                ParseImplements(type, kind),
                DependsOn(type, constructorTypes),
                constructorTypes));
        }

        return markers;
    }

    private static string KindOf(TypeDeclarationSyntax type) => type switch
    {
        InterfaceDeclarationSyntax => "interface",
        RecordDeclarationSyntax => "record",
        StructDeclarationSyntax => "struct",
        _ => "class",
    };

    private static IReadOnlyList<string> ParseImplements(TypeDeclarationSyntax type, string kind)
    {
        if (type.BaseList is null)
            return [];

        var parts = type.BaseList.Types
            .Select(t => SimpleTypeName(t.Type))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToArray();

        if (parts.Length == 0)
            return [];
        if (kind == "interface" || parts.Length == 1)
            return parts;

        return IsInterfaceName(parts[0]) ? parts : parts.Skip(1).ToArray();
    }

    private static IReadOnlyList<string> ConstructorParameterTypes(TypeDeclarationSyntax type)
    {
        var constructor = type.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        if (constructor is null)
            return [];

        return constructor.ParameterList.Parameters
            .Select(p => SimpleTypeName(p.Type))
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> DependsOn(TypeDeclarationSyntax type, IReadOnlyList<string> constructorTypes)
    {
        var depends = new HashSet<string>(constructorTypes, StringComparer.Ordinal);

        foreach (var property in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            foreach (var identifier in property.Type.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                var name = identifier.Identifier.ValueText;
                if (IsInterfaceName(name))
                    depends.Add(name);
            }
        }

        return depends.ToArray();
    }

    private static string? SimpleTypeName(TypeSyntax? type)
    {
        if (type is null)
            return null;

        var text = type.ToString();
        var generic = text.IndexOf('<');
        if (generic >= 0)
            text = text[..generic];
        text = text.TrimEnd('?').Trim();

        var dot = text.LastIndexOf('.');
        if (dot >= 0 && dot < text.Length - 1)
            text = text[(dot + 1)..];

        return text.Length == 0 ? null : text;
    }

    private static bool IsInterfaceName(string name)
        => name.Length >= 2 && name[0] == 'I' && char.IsUpper(name[1]);

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
