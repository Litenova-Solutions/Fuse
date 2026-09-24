using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Evals;

/// <summary>One edit to one file. <see cref="NewText"/> null means the file is deleted.</summary>
internal sealed record FileEdit(string Path, string Kind, string Description, string? NewText);

/// <summary>
///     Generates API-shape mutations (the edits that break other code) with Roslyn syntax rewriting: removing,
///     renaming, re-parameterizing or hiding a member, duplicating a signature, changing a return type, removing a
///     using directive, or deleting a file.
/// </summary>
internal static class CompileMutator
{
    public static readonly string[] Kinds =
        ["remove-member", "rename-member", "change-parameters", "make-private", "duplicate-signature", "change-return-type", "remove-using", "delete-file"];

    /// <summary>Tries to produce one mutation of <paramref name="kind"/> in <paramref name="path"/>; null when the file has no suitable target.</summary>
    public static FileEdit? Mutate(string path, string relative, string kind, Random random)
    {
        var text = File.ReadAllText(path);
        var root = CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview)).GetCompilationUnitRoot();
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.Parent is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax
                        && (m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Modifiers.Any(SyntaxKind.InternalKeyword))
                        && !m.Modifiers.Any(SyntaxKind.OverrideKeyword)
                        && !m.Modifiers.Any(SyntaxKind.PartialKeyword)
                        && m.ExplicitInterfaceSpecifier is null)
            .ToList();
        MethodDeclarationSyntax? Pick(Func<MethodDeclarationSyntax, bool> filter)
        {
            var candidates = methods.Where(filter).ToList();
            return candidates.Count == 0 ? null : candidates[random.Next(candidates.Count)];
        }

        string Name(MethodDeclarationSyntax m) => $"{(m.Parent as BaseTypeDeclarationSyntax)?.Identifier.Text}.{m.Identifier.Text}";

        switch (kind)
        {
            case "remove-member":
                {
                    var m = Pick(_ => true);
                    return m is null ? null : new FileEdit(relative, kind, $"removed {Name(m)}", root.RemoveNode(m, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString());
                }

            case "rename-member":
                {
                    var m = Pick(_ => true);
                    return m is null ? null : new FileEdit(relative, kind, $"renamed {Name(m)} to {m.Identifier.Text}Renamed",
                        root.ReplaceToken(m.Identifier, SyntaxFactory.Identifier(m.Identifier.Text + "Renamed").WithTriviaFrom(m.Identifier)).ToFullString());
                }

            case "change-parameters":
                {
                    var m = Pick(_ => true);
                    if (m is null)
                        return null;
                    var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier("fuseExtra")).WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)).WithTrailingTrivia(SyntaxFactory.Space));
                    var list = m.ParameterList.Parameters.Count > 0 && m.ParameterList.Parameters.Last().Modifiers.Any(SyntaxKind.ParamsKeyword)
                        ? m.ParameterList.WithParameters(m.ParameterList.Parameters.Insert(0, parameter))
                        : m.ParameterList.AddParameters(parameter);
                    return new FileEdit(relative, kind, $"added an int parameter to {Name(m)}", root.ReplaceNode(m.ParameterList, list).ToFullString());
                }

            case "make-private":
                {
                    var m = Pick(_ => true);
                    if (m is null)
                        return null;
                    var modifiers = SyntaxFactory.TokenList(m.Modifiers.Where(t => !t.IsKind(SyntaxKind.PublicKeyword) && !t.IsKind(SyntaxKind.InternalKeyword) && !t.IsKind(SyntaxKind.ProtectedKeyword))
                        .Prepend(SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithTrailingTrivia(SyntaxFactory.Space)));
                    var leading = m.Modifiers.First().LeadingTrivia;
                    modifiers = modifiers.Replace(modifiers[0], modifiers[0].WithLeadingTrivia(leading));
                    return new FileEdit(relative, kind, $"made {Name(m)} private", root.ReplaceNode(m, m.WithModifiers(modifiers)).ToFullString());
                }

            case "duplicate-signature":
                {
                    var m = Pick(x => x.Body is not null || x.ExpressionBody is not null);
                    if (m is null)
                        return null;
                    var copy = m.WithLeadingTrivia(SyntaxFactory.EndOfLine("\n")).WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));
                    var type = (TypeDeclarationSyntax)m.Parent!;
                    var updated = type.WithMembers(type.Members.Insert(type.Members.IndexOf(m) + 1, copy));
                    return new FileEdit(relative, kind, $"duplicated the signature of {Name(m)}", root.ReplaceNode(type, updated).ToFullString());
                }

            case "change-return-type":
                {
                    var m = Pick(x => x.ReturnType is not PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword } && x.Modifiers.All(t => !t.IsKind(SyntaxKind.AsyncKeyword))
                                     && x.ReturnType.ToString() is not ("object" or "Task" or "ValueTask" or "IEnumerable"));
                    if (m is null)
                        return null;
                    return new FileEdit(relative, kind, $"changed the return type of {Name(m)} from {m.ReturnType} to object",
                        root.ReplaceNode(m.ReturnType, SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)).WithTriviaFrom(m.ReturnType)).ToFullString());
                }

            case "remove-using":
                {
                    var usings = root.Usings.Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None) && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Alias is null).ToList();
                    if (usings.Count == 0)
                        return null;
                    var u = usings[random.Next(usings.Count)];
                    return new FileEdit(relative, kind, $"removed using {u.Name}", root.RemoveNode(u, SyntaxRemoveOptions.KeepNoTrivia)!.ToFullString());
                }

            case "delete-file":
                return root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Any()
                    ? new FileEdit(relative, kind, "deleted the file", null)
                    : null;
        }

        return null;
    }
}
