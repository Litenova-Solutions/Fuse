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
    ///     that lost a member, and the compilation unit when its top-level statements changed. When file-level code (usings,
    ///     attributes) changed, every type in the file is returned. A field or an event field is returned as its whole
    ///     declaration, with every variable in it.
    /// </summary>
    /// <param name="before">The file at HEAD, or null when HEAD does not have it.</param>
    /// <param name="after">The file in the working tree.</param>
    public static IReadOnlyList<SyntaxNode> Find(SyntaxNode? before, SyntaxNode after)
    {
        var old = before is null ? FileDeclarations.Empty : FileDeclarations.Of(before);
        var now = FileDeclarations.Of(after);
        var result = new List<SyntaxNode>();

        if (before is CompilationUnitSyntax oldUnit && after is CompilationUnitSyntax newUnit
            && (!Equivalent(oldUnit.Usings, newUnit.Usings) || !Equivalent(oldUnit.AttributeLists, newUnit.AttributeLists)))
        {
            result.AddRange(after.DescendantNodes().OfType<BaseTypeDeclarationSyntax>());
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
            else if (!SyntaxFactory.AreEquivalent(CodeOf(previous), CodeOf(declaration), topLevel: false))
                result.Add(CodeOf(declaration));
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

    private static bool Equivalent<T>(SyntaxList<T> a, SyntaxList<T> b)
        where T : SyntaxNode =>
        a.Count == b.Count && a.Zip(b).All(p => SyntaxFactory.AreEquivalent(p.First, p.Second, topLevel: false));

    /// <summary>
    ///     The declarations that hold code, which leaves out the using directives and file-level attribute lists that
    ///     <see cref="Find"/> compares as whole lists. The top-level statements come first, then each type followed by its
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

    /// <summary>The syntax whose code is compared and returned: a field's whole declaration for one of its variables, and the declaring node otherwise.</summary>
    private static SyntaxNode CodeOf(DeclarationNode declaration) =>
        declaration.Node is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax field } ? field : declaration.Node;
}
