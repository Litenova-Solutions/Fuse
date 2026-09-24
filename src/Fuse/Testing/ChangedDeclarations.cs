using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Testing;

/// <summary>Finds the declarations whose code differs between two versions of a C# file.</summary>
internal static class ChangedDeclarations
{
    /// <summary>
    ///     Returns nodes in <paramref name="after"/> for every member whose text changed or that was added, every type
    ///     whose header changed or that lost a member, and every top-level statement block that changed. When file-level
    ///     code (usings, attributes) changed, every type in the file is returned.
    /// </summary>
    public static IReadOnlyList<SyntaxNode> Find(SyntaxNode? before, SyntaxNode after)
    {
        var oldMembers = before is null ? new Dictionary<string, SyntaxNode>() : Index(before);
        var newMembers = Index(after);
        var result = new List<SyntaxNode>();

        if (before is CompilationUnitSyntax oldUnit && after is CompilationUnitSyntax newUnit
            && (!Equivalent(oldUnit.Usings, newUnit.Usings) || !Equivalent(oldUnit.AttributeLists, newUnit.AttributeLists)))
        {
            result.AddRange(after.DescendantNodes().OfType<BaseTypeDeclarationSyntax>());
        }

        foreach (var (key, node) in newMembers)
        {
            if (!oldMembers.TryGetValue(key, out var old))
                result.Add(node);
            else if (node is BaseTypeDeclarationSyntax newType && old is BaseTypeDeclarationSyntax oldType)
            {
                if (Header(oldType) != Header(newType))
                    result.Add(node);
            }
            else if (!SyntaxFactory.AreEquivalent(old, node, topLevel: false))
                result.Add(node);
        }

        foreach (var (key, old) in oldMembers)
        {
            if (newMembers.ContainsKey(key))
                continue;
            // A removed member: whatever used it now binds differently, and it was reached through its type.
            var typeKey = key[..Math.Max(0, key.LastIndexOf('|'))];
            if (newMembers.TryGetValue(typeKey, out var type))
                result.Add(type);
        }

        return result.Distinct().ToList();
    }

    /// <summary>A type declaration without its members and trivia.</summary>
    private static string Header(BaseTypeDeclarationSyntax type)
    {
        var parts = new List<string> { string.Join(" ", type.AttributeLists.Select(a => a.NormalizeWhitespace().ToString())), type.Modifiers.ToString(), type.Identifier.Text };
        if (type is TypeDeclarationSyntax t)
        {
            parts.Add(t.TypeParameterList?.NormalizeWhitespace().ToString() ?? "");
            parts.Add(t.ParameterList?.NormalizeWhitespace().ToString() ?? "");
            parts.Add(string.Join(" ", t.ConstraintClauses.Select(c => c.NormalizeWhitespace().ToString())));
        }

        parts.Add(type.BaseList?.NormalizeWhitespace().ToString() ?? "");
        return string.Join("|", parts);
    }

    private static bool Equivalent<T>(SyntaxList<T> a, SyntaxList<T> b)
        where T : SyntaxNode =>
        a.Count == b.Count && a.Zip(b).All(p => SyntaxFactory.AreEquivalent(p.First, p.Second, topLevel: false));

    /// <summary>Keys every member and type. Types are keyed by their header only, so a changed member does not mark its type changed.</summary>
    private static Dictionary<string, SyntaxNode> Index(SyntaxNode root)
    {
        var result = new Dictionary<string, SyntaxNode>(StringComparer.Ordinal);
        var globals = root.ChildNodes().OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count > 0)
            result["<top-level>"] = globals[0].Parent!;
        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var typeKey = TypeKey(type);
            result.TryAdd(typeKey, type);
            if (type is not TypeDeclarationSyntax withMembers)
            {
                if (type is EnumDeclarationSyntax enumDeclaration)
                    foreach (var member in enumDeclaration.Members)
                        result.TryAdd($"{typeKey}|{member.Identifier.Text}", member);
                continue;
            }

            foreach (var member in withMembers.Members.Where(m => m is not BaseTypeDeclarationSyntax))
                result.TryAdd($"{typeKey}|{MemberKey(member)}", member);
        }

        return result;
    }

    private static string TypeKey(BaseTypeDeclarationSyntax type)
    {
        var parts = new Stack<string>();
        for (SyntaxNode? node = type; node is not null; node = node.Parent)
        {
            switch (node)
            {
                case BaseTypeDeclarationSyntax t:
                    parts.Push(t.Identifier.Text + (t is TypeDeclarationSyntax { TypeParameterList: { } tp } ? "`" + tp.Parameters.Count : ""));
                    break;
                case BaseNamespaceDeclarationSyntax ns:
                    parts.Push(ns.Name.ToString());
                    break;
            }
        }

        return string.Join(".", parts);
    }

    private static string MemberKey(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => $"M:{m.Identifier.Text}`{m.TypeParameterList?.Parameters.Count ?? 0}({Parameters(m.ParameterList)})",
        ConstructorDeclarationSyntax c => $"C:{(c.Modifiers.Any(SyntaxKind.StaticKeyword) ? "static" : "")}({Parameters(c.ParameterList)})",
        DestructorDeclarationSyntax => "D:",
        PropertyDeclarationSyntax p => $"P:{p.Identifier.Text}",
        IndexerDeclarationSyntax i => $"I:[{Parameters(i.ParameterList)}]",
        EventDeclarationSyntax e => $"E:{e.Identifier.Text}",
        EventFieldDeclarationSyntax e => $"E:{string.Join(",", e.Declaration.Variables.Select(v => v.Identifier.Text))}",
        FieldDeclarationSyntax f => $"F:{string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.Text))}",
        OperatorDeclarationSyntax o => $"O:{o.OperatorToken.Text}({Parameters(o.ParameterList)})",
        ConversionOperatorDeclarationSyntax c => $"O:{c.ImplicitOrExplicitKeyword.Text}({c.Type})",
        _ => $"?:{member.SpanStart}",
    };

    private static string Parameters(BaseParameterListSyntax list) =>
        string.Join(",", list.Parameters.Select(p => p.Type?.ToString() ?? ""));
}
