using Fuse.Plugins.Abstractions.Dependencies;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Plugins.Languages.CSharp.Roslyn;

/// <summary>
///     Roslyn-based <see cref="IDependencyExtractor" />. Collects referenced type names from the parsed syntax
///     tree rather than by regex, so it captures references the regex extractor misses (return types, generic
///     arguments, object creations, base types, attributes) and does not create edges from text in comments or
///     strings, which the parser classifies as trivia.
/// </summary>
/// <remarks>
///     This is syntax-level, not full semantic binding: it identifies type-position identifiers without
///     resolving them across a compilation. It is a substantially more accurate approximation than the regex
///     extractor, not a guaranteed-complete call graph.
/// </remarks>
public sealed class RoslynDependencyExtractor : IDependencyExtractor
{
    private static readonly HashSet<string> Primitives = new(StringComparer.Ordinal)
    {
        "String", "Int32", "Boolean", "Object", "Void", "Task", "ValueTask",
    };

    /// <inheritdoc />
    public IReadOnlyCollection<string> SupportedExtensions { get; } = [".cs"];

    /// <inheritdoc />
    public IReadOnlyList<string> ExtractReferencedTypes(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
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

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case GenericNameSyntax generic:
                    AddName(names, generic.Identifier.ValueText);
                    break;

                case IdentifierNameSyntax id:
                    if (IsTypePosition(id))
                        AddName(names, id.Identifier.ValueText);
                    break;
            }
        }

        return names.ToArray();
    }

    // Keeps PascalCase identifiers in type position and skips the namespace-qualifier left side and the member
    // name of a member access, which are not type references. The PascalCase filter drops camelCase locals,
    // parameters, and fields that share the identifier-name node shape.
    private static bool IsTypePosition(IdentifierNameSyntax id)
    {
        var name = id.Identifier.ValueText;
        if (name.Length == 0 || !char.IsUpper(name[0]))
            return false;

        switch (id.Parent)
        {
            // Left side of A.B (namespace or nested type qualifier) is not the referenced type itself.
            case QualifiedNameSyntax qualified when qualified.Left == id:
                return false;
            // The member being accessed (x.Member) is not a type.
            case MemberAccessExpressionSyntax member when member.Name == id:
                return false;
            default:
                return true;
        }
    }

    private static void AddName(HashSet<string> names, string name)
    {
        if (name.Length > 0 && char.IsUpper(name[0]) && !Primitives.Contains(name))
            names.Add(name);
    }
}
