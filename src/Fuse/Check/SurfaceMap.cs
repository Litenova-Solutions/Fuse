using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Check;

/// <summary>One declaration's externally visible shape.</summary>
/// <param name="Signature">Everything about the declaration that other files can observe, with bodies and trivia removed.</param>
/// <param name="Names">Identifiers other code would use to reach it: the member name and its containing type's name.</param>
/// <param name="Node">The declaring syntax node, for resolving the declared symbol.</param>
/// <param name="Container">The key of the containing type's entry, or null at file level.</param>
internal sealed record SurfaceEntry(string Signature, string[] Names, SyntaxNode? Node = null, string? Container = null);

/// <summary>One declaration that differs between two versions of a file.</summary>
/// <param name="Key">The declaration's identity.</param>
/// <param name="Before">The HEAD entry, or null when the declaration exists only in the working tree.</param>
/// <param name="After">The working-tree entry, or null when the declaration exists only at HEAD.</param>
internal sealed record SurfaceChange(string Key, SurfaceEntry? Before, SurfaceEntry? After);

/// <summary>
///     The declarations a C# file exposes to other files, keyed by a stable identity. Comparing the maps of a file at
///     HEAD and in the working tree tells which declarations changed, without binding.
/// </summary>
internal static class SurfaceMap
{
    public static Dictionary<string, SurfaceEntry> Compute(SyntaxNode root)
    {
        var map = new Dictionary<string, SurfaceEntry>(StringComparer.Ordinal);
        if (root is CompilationUnitSyntax unit)
        {
            foreach (var global in unit.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)))
                map["G:" + Flat(global)] = new SurfaceEntry(Flat(global), [], global);
            foreach (var list in unit.AttributeLists)
                map["A:" + Flat(list)] = new SurfaceEntry(Flat(list), [], list);
        }

        Walk(root, "", map);
        return map;
    }

    /// <summary>The declarations that differ between two versions of a file.</summary>
    public static List<SurfaceChange> Changes(Dictionary<string, SurfaceEntry> before, Dictionary<string, SurfaceEntry> after)
    {
        var result = new List<SurfaceChange>();
        foreach (var key in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(key, out var old);
            after.TryGetValue(key, out var now);
            if (old is null || now is null || old.Signature != now.Signature)
                result.Add(new SurfaceChange(key, old, now));
        }

        return result;
    }

    private static void Walk(SyntaxNode node, string container, Dictionary<string, SurfaceEntry> map)
    {
        foreach (var child in node.ChildNodes())
        {
            switch (child)
            {
                case BaseNamespaceDeclarationSyntax ns:
                    Walk(ns, Qualify(container, ns.Name.ToString()), map);
                    break;
                case DelegateDeclarationSyntax del:
                    map[$"T:{Qualify(container, del.Identifier.Text)}`{Arity(del.TypeParameterList)}"] =
                        new SurfaceEntry(Flat(del), [del.Identifier.Text], del);
                    break;
                case BaseTypeDeclarationSyntax type:
                    AddType(type, container, map);
                    break;
            }
        }
    }

    private static void AddType(BaseTypeDeclarationSyntax type, string container, Dictionary<string, SurfaceEntry> map)
    {
        var name = type.Identifier.Text;
        var typeParams = type is TypeDeclarationSyntax t ? t.TypeParameterList : null;
        var qualified = $"{Qualify(container, name)}`{Arity(typeParams)}";
        var header = new StringBuilder();
        header.Append(Flat(type.AttributeLists)).Append(' ').Append(type.Modifiers.ToString()).Append(' ');
        header.Append(type switch
        {
            RecordDeclarationSyntax r => r.Keyword.Text + r.ClassOrStructKeyword.Text,
            TypeDeclarationSyntax td => td.Keyword.Text,
            EnumDeclarationSyntax e => e.EnumKeyword.Text,
            _ => "",
        });
        header.Append(' ').Append(name).Append(Flat(typeParams));
        if (type is TypeDeclarationSyntax withParams)
            header.Append(Flat(withParams.ParameterList)).Append(Flat(withParams.ConstraintClauses));
        header.Append(Flat(type.BaseList));
        map["T:" + qualified] = new SurfaceEntry(header.ToString(), [name], type, container.Length == 0 ? null : "T:" + container);

        if (type is EnumDeclarationSyntax enumDeclaration)
        {
            foreach (var member in enumDeclaration.Members)
                map[$"F:{qualified}.{member.Identifier.Text}"] = new SurfaceEntry(Flat(member), [member.Identifier.Text, name], member, "T:" + qualified);
            return;
        }

        if (type is not TypeDeclarationSyntax typeDeclaration)
            return;
        foreach (var member in typeDeclaration.Members)
            AddMember(member, qualified, name, map);
    }

    private static void AddMember(MemberDeclarationSyntax member, string qualified, string typeName, Dictionary<string, SurfaceEntry> map)
    {
        var prefix = Flat(member.AttributeLists) + " " + member.Modifiers;
        switch (member)
        {
            case BaseTypeDeclarationSyntax nested:
                AddType(nested, qualified, map);
                break;
            case DelegateDeclarationSyntax del:
                map[$"T:{qualified}.{del.Identifier.Text}`{Arity(del.TypeParameterList)}"] =
                    new SurfaceEntry(Flat(del), [del.Identifier.Text, typeName], del, "T:" + qualified);
                break;
            case MethodDeclarationSyntax method:
                map[$"M:{qualified}.{method.ExplicitInterfaceSpecifier}{method.Identifier.Text}`{Arity(method.TypeParameterList)}({ParameterTypes(method.ParameterList)})"] =
                    new SurfaceEntry(
                        $"{prefix} {Flat(method.ReturnType)} {method.Identifier.Text}{Flat(method.TypeParameterList)}{Flat(method.ParameterList)}{Flat(method.ConstraintClauses)}",
                        [method.Identifier.Text, typeName],
                        method,
                        "T:" + qualified);
                break;
            case ConstructorDeclarationSyntax ctor:
                map[$"C:{qualified}({ParameterTypes(ctor.ParameterList)})"] =
                    new SurfaceEntry($"{prefix} {Flat(ctor.ParameterList)}", [typeName], ctor, "T:" + qualified);
                break;
            case PropertyDeclarationSyntax property:
                map[$"P:{qualified}.{property.ExplicitInterfaceSpecifier}{property.Identifier.Text}"] =
                    new SurfaceEntry($"{prefix} {Flat(property.Type)} {property.Identifier.Text} {Accessors(property.AccessorList, property.ExpressionBody)}", [property.Identifier.Text, typeName], property, "T:" + qualified);
                break;
            case IndexerDeclarationSyntax indexer:
                map[$"I:{qualified}[{ParameterTypes(indexer.ParameterList)}]"] =
                    new SurfaceEntry($"{prefix} {Flat(indexer.Type)} {Flat(indexer.ParameterList)} {Accessors(indexer.AccessorList, indexer.ExpressionBody)}", [typeName], indexer, "T:" + qualified);
                break;
            case EventDeclarationSyntax evt:
                map[$"E:{qualified}.{evt.Identifier.Text}"] = new SurfaceEntry($"{prefix} {Flat(evt.Type)}", [evt.Identifier.Text, typeName], evt, "T:" + qualified);
                break;
            case EventFieldDeclarationSyntax eventField:
                foreach (var variable in eventField.Declaration.Variables)
                    map[$"E:{qualified}.{variable.Identifier.Text}"] = new SurfaceEntry($"{prefix} {Flat(eventField.Declaration.Type)}", [variable.Identifier.Text, typeName], variable, "T:" + qualified);
                break;
            case FieldDeclarationSyntax field:
                var isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword);
                foreach (var variable in field.Declaration.Variables)
                {
                    // A constant's value is part of its surface: it is inlined into every user.
                    var value = isConst ? Flat(variable.Initializer) : "";
                    map[$"F:{qualified}.{variable.Identifier.Text}"] = new SurfaceEntry($"{prefix} {Flat(field.Declaration.Type)} {value}", [variable.Identifier.Text, typeName], variable, "T:" + qualified);
                }

                break;
            case OperatorDeclarationSyntax op:
                map[$"O:{qualified}.{op.OperatorToken.Text}({ParameterTypes(op.ParameterList)})"] =
                    new SurfaceEntry($"{prefix} {Flat(op.ReturnType)} {Flat(op.ParameterList)}", [typeName], op, "T:" + qualified);
                break;
            case ConversionOperatorDeclarationSyntax conversion:
                map[$"O:{qualified}.{conversion.ImplicitOrExplicitKeyword.Text}({Flat(conversion.Type)})"] =
                    new SurfaceEntry($"{prefix} {Flat(conversion.ParameterList)}", [typeName], conversion, "T:" + qualified);
                break;
        }
    }

    private static string Accessors(AccessorListSyntax? accessors, ArrowExpressionClauseSyntax? expressionBody)
    {
        if (accessors is null)
            return expressionBody is null ? "" : "get";
        return string.Join(' ', accessors.Accessors.Select(a => $"{Flat(a.AttributeLists)} {a.Modifiers} {a.Keyword.Text}"));
    }

    private static string ParameterTypes(BaseParameterListSyntax? list) =>
        list is null ? "" : string.Join(",", list.Parameters.Select(p => $"{p.Modifiers} {Flat(p.Type)}"));

    private static int Arity(TypeParameterListSyntax? list) => list?.Parameters.Count ?? 0;

    private static string Qualify(string container, string name) => container.Length == 0 ? name : container + "." + name;

    /// <summary>Text of a node with all trivia collapsed, so formatting and comments never register as changes.</summary>
    private static string Flat(SyntaxNode? node)
    {
        if (node is null)
            return "";
        var builder = new StringBuilder();
        foreach (var token in node.DescendantTokens())
        {
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(token.Text);
        }

        return builder.ToString();
    }

    private static string Flat<T>(SyntaxList<T> list)
        where T : SyntaxNode => string.Join(" ", list.Select(n => Flat(n)));
}
