using Fuse.Plugins.Abstractions.Outline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Plugins.Languages.CSharp.Roslyn;

/// <summary>
///     Roslyn-based <see cref="ISymbolOutlineExtractor" />. Produces a precise type-and-member outline from the
///     parsed tree, attributing each member to its declaring type without the regex extractor's brace counting.
/// </summary>
public sealed class RoslynOutlineExtractor : ISymbolOutlineExtractor
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public IReadOnlyList<OutlineSymbol> ExtractOutline(string content)
    {
        if (string.IsNullOrEmpty(content))
            return [];

        SyntaxNode root;
        try
        {
            root = CSharpSyntaxTree.ParseText(content).GetRoot();
        }
        catch
        {
            return [];
        }

        var outline = new List<OutlineSymbol>();
        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            outline.Add(new OutlineSymbol(KindOf(type), type.Identifier.ValueText, MembersOf(type)));

        return outline;
    }

    private static string KindOf(BaseTypeDeclarationSyntax type) => type switch
    {
        InterfaceDeclarationSyntax => "interface",
        RecordDeclarationSyntax => "record",
        StructDeclarationSyntax => "struct",
        EnumDeclarationSyntax => "enum",
        _ => "class",
    };

    private static IReadOnlyList<string> MembersOf(BaseTypeDeclarationSyntax type)
    {
        var members = new List<string>();

        if (type is EnumDeclarationSyntax enumType)
        {
            foreach (var member in enumType.Members)
                members.Add(member.Identifier.ValueText);
            return members;
        }

        if (type is not TypeDeclarationSyntax typeDecl)
            return members;

        // Only members declared directly on this type, not those of nested types (which appear as their own
        // outline entries).
        foreach (var member in typeDecl.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax m:
                    members.Add(m.Identifier.ValueText);
                    break;
                case PropertyDeclarationSyntax p:
                    members.Add(p.Identifier.ValueText);
                    break;
                case ConstructorDeclarationSyntax c:
                    members.Add(c.Identifier.ValueText);
                    break;
                case EventDeclarationSyntax e:
                    members.Add(e.Identifier.ValueText);
                    break;
                case FieldDeclarationSyntax f:
                    foreach (var v in f.Declaration.Variables)
                        members.Add(v.Identifier.ValueText);
                    break;
            }
        }

        return members;
    }
}
