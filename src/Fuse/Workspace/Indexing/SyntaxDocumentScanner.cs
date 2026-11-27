using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
namespace Fuse.Workspace.Indexing;

/// <summary>
///     Turns one document's syntax root into the tokens and types the index holds, without loading any semantic model.
/// </summary>
internal static class SyntaxDocumentScanner
{
    /// <summary>The tokens and types scanned from one document, before they are stored under its path and preprocessor entry.</summary>
    /// <param name="Tokens">Identifier token texts, with the <c>Attribute</c> suffix form added for attribute names.</param>
    /// <param name="BaseTypes">Simple names of the base types and interfaces each declared type names.</param>
    /// <param name="DeclaredTypes">Simple names of the types each declaration declares.</param>
    /// <param name="GlobalAliases">Global using alias pairs that map an alias to its target type name.</param>
    internal sealed record ScanResult(
        string[] Tokens,
        string[] BaseTypes,
        string[] DeclaredTypes,
        (string Alias, string Target)[] GlobalAliases);

    internal static ScanResult Scan(SyntaxNode root)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var baseTypes = new HashSet<string>(StringComparer.Ordinal);
        var declaredTypes = new HashSet<string>(StringComparer.Ordinal);
        var localAliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var globalAliases = new List<(string Alias, string Target)>();

        foreach (var token in root.DescendantTokens(descendIntoTrivia: true))
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken))
                continue;
            var valueText = token.ValueText;
            if (string.IsNullOrEmpty(valueText))
                continue;
            tokens.Add(valueText);

            if (IsAttributeNameToken(token) && !valueText.EndsWith("Attribute", StringComparison.Ordinal))
                tokens.Add(valueText + "Attribute");
        }

        WalkTypeHierarchy(root, tokens, baseTypes, declaredTypes, localAliases, globalAliases);
        return new ScanResult([.. tokens], [.. baseTypes], [.. declaredTypes], [.. globalAliases]);
    }

    private static bool IsAttributeNameToken(SyntaxToken token)
    {
        if (token.Parent is not SimpleNameSyntax name)
            return false;
        return name.Parent switch
        {
            AttributeSyntax attribute => attribute.Name == name,
            QualifiedNameSyntax qualified => qualified.Right == name && qualified.Parent is AttributeSyntax attribute && attribute.Name == qualified,
            AliasQualifiedNameSyntax alias => alias.Name == name && alias.Parent is AttributeSyntax attribute && attribute.Name == alias,
            _ => false,
        };
    }

    private static void WalkTypeHierarchy(
        SyntaxNode node,
        HashSet<string> tokens,
        HashSet<string> baseTypes,
        HashSet<string> declaredTypes,
        Dictionary<string, string> localAliases,
        List<(string Alias, string Target)> globalAliases)
    {
        foreach (var child in node.ChildNodes())
        {
            switch (child)
            {
                case UsingDirectiveSyntax directive when directive.Alias is { } alias && directive.NamespaceOrType is { } targetNode:
                    if (RightmostIdentifier(targetNode) is { } targetName)
                    {
                        var aliasName = alias.Name.Identifier.ValueText;
                        if (directive.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword))
                            globalAliases.Add((aliasName, targetName));
                        else
                            localAliases[aliasName] = targetName;
                    }

                    break;

                case BaseNamespaceDeclarationSyntax ns:
                    WalkTypeHierarchy(ns, tokens, baseTypes, declaredTypes, localAliases, globalAliases);
                    break;

                case DelegateDeclarationSyntax del:
                    declaredTypes.Add(del.Identifier.ValueText);
                    break;

                case BaseTypeDeclarationSyntax type:
                    if (type is not ExtensionBlockDeclarationSyntax)
                        declaredTypes.Add(type.Identifier.ValueText);
                    if (type.BaseList is { } baseList)
                    {
                        foreach (var bt in baseList.Types)
                        {
                            if (RightmostIdentifier(bt.Type) is not { } baseName)
                                continue;
                            baseTypes.Add(baseName);
                            if (localAliases.TryGetValue(baseName, out var resolved))
                                baseTypes.Add(resolved);
                        }
                    }

                    if (type is TypeDeclarationSyntax typeDeclaration)
                        WalkTypeHierarchy(typeDeclaration, tokens, baseTypes, declaredTypes, localAliases, globalAliases);
                    break;
            }
        }
    }

    private static string? RightmostIdentifier(TypeSyntax type) =>
        type switch
        {
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            _ => null,
        };
}
