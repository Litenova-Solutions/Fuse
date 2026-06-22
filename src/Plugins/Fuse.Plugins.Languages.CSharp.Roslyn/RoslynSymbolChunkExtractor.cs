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
            span.EndLinePosition.Line + 1);
    }
}
