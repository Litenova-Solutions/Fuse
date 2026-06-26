using Fuse.Plugins.Abstractions.Outline;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Plugins.Languages.CSharp.Roslyn;

/// <summary>
///     Roslyn-based <see cref="ISymbolChunkExtractor" />. Produces one chunk per declared member from the parsed
///     tree, attributing each to its declaring type and recording its precise source span.
/// </summary>
public sealed class RoslynSymbolChunkExtractor : ISymbolChunkExtractor
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public IReadOnlyList<SymbolChunk> ExtractChunks(string content)
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

        var chunks = new List<SymbolChunk>();
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var parent = type.Identifier.ValueText;

            // Members declared directly on this type. Nested types are walked as their own type entries, so
            // their members are not double-counted here.
            foreach (var member in type.Members)
            {
                switch (member)
                {
                    case MethodDeclarationSyntax m:
                        chunks.Add(Chunk("method", m.Identifier.ValueText, parent, member));
                        break;
                    case ConstructorDeclarationSyntax c:
                        chunks.Add(Chunk("constructor", c.Identifier.ValueText, parent, member));
                        break;
                    case PropertyDeclarationSyntax p:
                        chunks.Add(Chunk("property", p.Identifier.ValueText, parent, member));
                        break;
                    case EventDeclarationSyntax e:
                        chunks.Add(Chunk("event", e.Identifier.ValueText, parent, member));
                        break;
                    case FieldDeclarationSyntax f:
                        // A multi-declarator field (for example `int a, b;`) is one chunk named after its first
                        // variable; splitting it would break the body text.
                        var first = f.Declaration.Variables.FirstOrDefault();
                        if (first is not null)
                            chunks.Add(Chunk("field", first.Identifier.ValueText, parent, member));
                        break;
                }
            }
        }

        foreach (var enumType in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            foreach (var member in enumType.Members)
                chunks.Add(Chunk("enum-member", member.Identifier.ValueText, enumType.Identifier.ValueText, member));
        }

        return chunks;
    }

    private static SymbolChunk Chunk(string kind, string name, string parent, SyntaxNode member)
    {
        var span = member.GetLocation().GetLineSpan();
        // ToString() omits leading trivia (comments, blank lines) so the fragment parses standalone; the line
        // span comes from the full node, so it includes attributes and modifiers.
        return new SymbolChunk(
            kind,
            name,
            parent,
            member.ToString(),
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1,
            BuildStableId(member, name));
    }

    // Builds a collision-free identity: namespace, the containing-type chain (each with generic arity), the
    // member name, and for methods and constructors the generic arity and parameter type list. This separates
    // overloads, like-named members of nested or different-namespace types, and partial-class members that the
    // display QualifiedName would conflate.
    private static string BuildStableId(SyntaxNode member, string name)
    {
        var prefix = new List<string>();

        var ns = member.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns is not null)
            prefix.Add(ns.Name.ToString());

        // Outermost type first, so the chain reads namespace-to-member left to right.
        foreach (var type in member.Ancestors().OfType<TypeDeclarationSyntax>().Reverse())
        {
            var arity = type.TypeParameterList?.Parameters.Count ?? 0;
            prefix.Add(arity > 0 ? $"{type.Identifier.ValueText}`{arity}" : type.Identifier.ValueText);
        }

        var enumType = member.Ancestors().OfType<EnumDeclarationSyntax>().FirstOrDefault();
        if (enumType is not null)
            prefix.Add(enumType.Identifier.ValueText);

        var head = prefix.Count > 0 ? string.Join(".", prefix) + "." + name : name;

        return member switch
        {
            MethodDeclarationSyntax m =>
                $"{head}{GenericArity(m.TypeParameterList)}({ParameterTypes(m.ParameterList)})",
            ConstructorDeclarationSyntax c => $"{head}({ParameterTypes(c.ParameterList)})",
            _ => head,
        };
    }

    private static string GenericArity(TypeParameterListSyntax? typeParameters)
    {
        var count = typeParameters?.Parameters.Count ?? 0;
        return count > 0 ? $"`{count}" : string.Empty;
    }

    private static string ParameterTypes(ParameterListSyntax parameters) =>
        string.Join(",", parameters.Parameters.Select(p => p.Type?.ToString() ?? "?"));
}
