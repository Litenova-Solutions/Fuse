using Fuse.Changes.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Changes;

/// <summary>
///     Which declarations' code differs between two versions of a C# file: test selection's question. Unlike
///     <see cref="SurfaceDiff"/> it counts a body edit, because a test can observe what a body does.
/// </summary>
internal static class CodeDiff
{
    /// <summary>
    ///     Returns nodes in <paramref name="after"/> for every added or changed member, every type whose header changed or
    ///     that lost a member, and the compilation unit when its top-level statements changed. When file-level code
    ///     (usings, inside a namespace or not, and file-level attributes) changed, the compilation unit, if the file has
    ///     top-level statements, and every type in the file are returned. A field or an event field is returned as its
    ///     whole declaration, with every variable in it.
    /// </summary>
    /// <param name="before">The file at HEAD, or null when HEAD does not have it.</param>
    /// <param name="after">The file in the working tree.</param>
    public static IReadOnlyList<SyntaxNode> Find(SyntaxNode? before, SyntaxNode after)
    {
        var old = before is null ? FileDeclarations.Empty : FileDeclarations.Of(before);
        var now = FileDeclarations.Of(after);
        var result = new List<SyntaxNode>();

        // Every declaration of an added file is returned below, so file-level code is compared only for a changed file.
        if (before is not null && !FileLevel(old).SetEquals(FileLevel(now)))
        {
            result.AddRange(now.All.Where(d => d.Key is DeclarationKey.TopLevelStatements).Select(d => d.Node));
            result.AddRange(now.All.Where(d => d.Node is BaseTypeDeclarationSyntax).SelectMany(d => d.Parts));
        }

        foreach (var declaration in WithCode(now))
        {
            var previous = old.Find(declaration.Key);
            if (previous is null)
                result.Add(CodeOf(declaration));
            else if (declaration.Node is BaseTypeDeclarationSyntax && previous.Node is BaseTypeDeclarationSyntax)
            {
                // A type is compared by its header only, so a changed member does not mark its type changed.
                if (previous.Surface != declaration.Surface)
                    result.Add(declaration.Node);
            }
            else if (declaration.Key is DeclarationKey.TopLevelStatements)
            {
                // The compilation unit holds the whole file, so only its statements are compared: a member added to a
                // class declared after them is not a change to them.
                if (!Equivalent(Statements(previous.Node), Statements(declaration.Node)))
                    result.Add(declaration.Node);
            }
            else
                result.AddRange(ChangedParts(previous, declaration));
        }

        foreach (var declaration in WithCode(old))
        {
            if (now.Find(declaration.Key) is not null || declaration.Node is BaseTypeDeclarationSyntax)
                continue;
            // A member gone from the working tree: its users bind differently, and they reach it through its type.
            if (declaration.Container is { } container && now.Find(container) is { } type)
                result.Add(type.Node);
        }

        return result.Distinct().ToList();
    }

    private static bool Equivalent(List<SyntaxNode> a, List<SyntaxNode> b) =>
        a.Count == b.Count && a.Zip(b).All(p => SyntaxFactory.AreEquivalent(p.First, p.Second, topLevel: false));

    private static List<SyntaxNode> Statements(SyntaxNode unit) => [.. unit.ChildNodes().OfType<GlobalStatementSyntax>()];

    /// <summary>
    ///     The file-level code of one version: each using directive with the namespace it is in, so moving one into or out
    ///     of a namespace is a change, and each global using and file-level attribute list. Order is left out, since it
    ///     changes what nothing binds to.
    /// </summary>
    private static HashSet<(DeclarationKey Key, string Namespace)> FileLevel(FileDeclarations declarations) =>
    [
        .. declarations.All
            .Where(d => d.Key is DeclarationKey.Using or DeclarationKey.GlobalUsing or DeclarationKey.AssemblyAttribute)
            .SelectMany(d => d.Parts.Select(p => (d.Key, NamespaceOf(p)))),
    ];

    /// <summary>The namespace <paramref name="directive"/> is declared in, containing namespaces first, or empty at file level.</summary>
    private static string NamespaceOf(SyntaxNode directive) =>
        string.Join('.', directive.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => string.Concat(n.Name.DescendantTokens().Select(t => t.Text))));

    /// <summary>
    ///     The declarations that hold code, which leaves out the using directives and file-level attribute lists that
    ///     <see cref="Find"/> compares as a set. The top-level statements come first, then each type followed by its
    ///     members, one type after another in the order the file declares them, so a nested type follows every member of
    ///     the type that contains it.
    /// </summary>
    private static IEnumerable<DeclarationNode> WithCode(FileDeclarations declarations) =>
        declarations.All
            .Where(d => d.Key is not (DeclarationKey.Using or DeclarationKey.GlobalUsing or DeclarationKey.AssemblyAttribute))
            .OrderBy(d => d.Node switch
            {
                CompilationUnitSyntax => -1,
                BaseTypeDeclarationSyntax type => type.SpanStart,
                var node => node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.SpanStart ?? node.SpanStart,
            });

    /// <summary>
    ///     The parts of <paramref name="current"/> whose code differs from the part in the same position at HEAD, such as
    ///     the implementation of a partial method declared in the same file as its definition. When HEAD had more parts
    ///     and the rest are unchanged, the first part stands for the one that is gone.
    /// </summary>
    private static IEnumerable<SyntaxNode> ChangedParts(DeclarationNode previous, DeclarationNode current)
    {
        var any = false;
        for (var i = 0; i < current.Parts.Count; i++)
        {
            var code = CodeOf(current.Parts[i]);
            if (i < previous.Parts.Count && SyntaxFactory.AreEquivalent(CodeOf(previous.Parts[i]), code, topLevel: false))
                continue;
            any = true;
            yield return code;
        }

        if (!any && previous.Parts.Count > current.Parts.Count)
            yield return CodeOf(current.Node);
    }

    /// <summary>The syntax whose code is compared and returned: a field's whole declaration for one of its variables, and the declaring node otherwise.</summary>
    private static SyntaxNode CodeOf(DeclarationNode declaration) => CodeOf(declaration.Node);

    private static SyntaxNode CodeOf(SyntaxNode node) =>
        node is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field } ? field : node;
}
