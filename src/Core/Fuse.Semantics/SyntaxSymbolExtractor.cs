using Fuse.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Fuse.Semantics;

/// <summary>
///     Extracts symbols and chunks from a single C# file using syntax analysis only (no semantic model).
/// </summary>
/// <remarks>
///     This is the Phase 2 approximation: type declarations are walked here for type-level symbols and chunks,
///     and member declarations are walked for member chunks (and their collision-free identities), from which
///     member symbols are derived. Identifiers are not resolved across files; symbol ids use the source-only
///     fallback form. MSBuild/Roslyn semantic indexing (Phase 3) replaces these ids with assembly-qualified ones.
/// </remarks>
public sealed class SyntaxSymbolExtractor
{
    /// <summary>
    ///     Extracts the symbols and chunks declared in a C# file.
    /// </summary>
    /// <param name="normalizedPath">The forward-slash relative path used to key records to the file.</param>
    /// <param name="content">The file's source text.</param>
    /// <returns>The extracted symbols and chunks; empty when the file declares nothing or fails to parse.</returns>
    public SyntaxExtractionResult Extract(string normalizedPath, string content)
    {
        if (string.IsNullOrEmpty(content))
            return SyntaxExtractionResult.Empty;

        SyntaxNode root;
        try
        {
            root = CSharpSyntaxTree.ParseText(content).GetRoot();
        }
        catch
        {
            return SyntaxExtractionResult.Empty;
        }

        var symbols = new List<SymbolRecord>();
        var chunks = new List<ChunkRecord>();

        // Type-level symbols and chunks. Each type contributes one symbol and one signature chunk; nested types
        // are reached because every TypeDeclarationSyntax/EnumDeclarationSyntax in the tree is visited.
        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var fqn = BuildTypeFqn(type);
            var kind = TypeKind(type);
            var accessibility = Accessibility(type.Modifiers);
            var span = type.GetLocation().GetLineSpan();
            var startLine = span.StartLinePosition.Line + 1;
            var endLine = span.EndLinePosition.Line + 1;
            var symbolId = FallbackSymbolId(normalizedPath, kind, fqn, startLine);
            var signature = TypeSignature(type);

            symbols.Add(new SymbolRecord(
                SymbolId: symbolId,
                FilePath: normalizedPath,
                Kind: kind,
                Name: type.Identifier.ValueText,
                FullyQualifiedName: fqn,
                ContainingType: ContainingTypeName(type),
                Namespace: NamespaceName(type),
                Accessibility: accessibility,
                Signature: signature,
                StartLine: startLine,
                EndLine: endLine,
                IsPublicApi: IsPublicApi(accessibility)));

            chunks.Add(new ChunkRecord(
                ChunkId: ChunkId(normalizedPath, kind, fqn, startLine),
                FilePath: normalizedPath,
                Kind: "type",
                StableKey: fqn,
                StartLine: startLine,
                EndLine: endLine,
                TextHash: HashEstimate(signature),
                TokenEstimate: EstimateTokens(signature),
                ReducedTokenEstimate: EstimateTokens(signature),
                SymbolId: symbolId,
                Name: type.Identifier.ValueText,
                Signature: signature,
                Body: signature,
                SymbolsText: type.Identifier.ValueText));
        }

        // Member chunks (and the member symbols derived from them) use the same collision-free identity rules as
        // the Roslyn structural plugin, inlined here so the semantics core does not reference plugin projects.
        foreach (var member in ExtractMemberChunks(root))
        {
            var symbolId = FallbackSymbolId(normalizedPath, member.Kind, member.Identity, member.StartLine);
            var signature = FirstLine(member.Content);

            symbols.Add(new SymbolRecord(
                SymbolId: symbolId,
                FilePath: normalizedPath,
                Kind: member.Kind,
                Name: member.Name,
                FullyQualifiedName: member.Identity,
                ContainingType: member.ParentType,
                Accessibility: null,
                Signature: signature,
                StartLine: member.StartLine,
                EndLine: member.EndLine,
                IsPublicApi: false));

            chunks.Add(new ChunkRecord(
                ChunkId: ChunkId(normalizedPath, member.Kind, member.Identity, member.StartLine),
                FilePath: normalizedPath,
                Kind: member.Kind,
                StableKey: member.Identity,
                StartLine: member.StartLine,
                EndLine: member.EndLine,
                TextHash: HashEstimate(member.Content),
                TokenEstimate: EstimateTokens(member.Content),
                ReducedTokenEstimate: EstimateTokens(signature),
                SymbolId: symbolId,
                Name: member.Name,
                Signature: signature,
                Body: member.Content,
                Comments: NullIfEmpty(CommentExtractor.Extract(member.Content)),
                SymbolsText: member.ParentType is null ? member.Name : $"{member.ParentType} {member.Name}"));
        }

        return new SyntaxExtractionResult(symbols, chunks);
    }

    private static IEnumerable<MemberChunk> ExtractMemberChunks(SyntaxNode root)
    {
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var parent = type.Identifier.ValueText;
            foreach (var member in type.Members)
            {
                switch (member)
                {
                    case MethodDeclarationSyntax m:
                        yield return CreateMemberChunk("method", m.Identifier.ValueText, parent, member);
                        break;
                    case ConstructorDeclarationSyntax c:
                        yield return CreateMemberChunk("constructor", c.Identifier.ValueText, parent, member);
                        break;
                    case PropertyDeclarationSyntax p:
                        yield return CreateMemberChunk("property", p.Identifier.ValueText, parent, member);
                        break;
                    case EventDeclarationSyntax e:
                        yield return CreateMemberChunk("event", e.Identifier.ValueText, parent, member);
                        break;
                    case FieldDeclarationSyntax f:
                        var first = f.Declaration.Variables.FirstOrDefault();
                        if (first is not null)
                            yield return CreateMemberChunk("field", first.Identifier.ValueText, parent, member);
                        break;
                }
            }
        }

        foreach (var enumType in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            foreach (var member in enumType.Members)
                yield return CreateMemberChunk("enum-member", member.Identifier.ValueText, enumType.Identifier.ValueText, member);
        }
    }

    private static MemberChunk CreateMemberChunk(string kind, string name, string parent, SyntaxNode member)
    {
        var span = member.GetLocation().GetLineSpan();
        return new MemberChunk(
            kind,
            name,
            parent,
            member.ToString(),
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1,
            BuildMemberStableId(member, name));
    }

    private static string BuildMemberStableId(SyntaxNode member, string name)
    {
        var prefix = new List<string>();

        var ns = member.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns is not null)
            prefix.Add(ns.Name.ToString());

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

    private sealed record MemberChunk(
        string Kind,
        string Name,
        string? ParentType,
        string Content,
        int StartLine,
        int EndLine,
        string Identity);

    private static string FallbackSymbolId(string normalizedPath, string kind, string stableKey, int line) =>
        $"symbol:fallback:{normalizedPath}:{kind}:{stableKey}:{line}";

    private static string ChunkId(string normalizedPath, string kind, string stableKey, int line) =>
        $"chunk:{normalizedPath}:{kind}:{stableKey}:{line}";

    private static string TypeKind(BaseTypeDeclarationSyntax type) => type switch
    {
        InterfaceDeclarationSyntax => "interface",
        StructDeclarationSyntax => "struct",
        RecordDeclarationSyntax => "record",
        EnumDeclarationSyntax => "enum",
        ClassDeclarationSyntax => "class",
        _ => "type",
    };

    private static string BuildTypeFqn(BaseTypeDeclarationSyntax type)
    {
        var parts = new List<string>();
        var ns = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns is not null)
            parts.Add(ns.Name.ToString());

        foreach (var outer in type.Ancestors().OfType<TypeDeclarationSyntax>().Reverse())
            parts.Add(outer.Identifier.ValueText);

        parts.Add(type.Identifier.ValueText);
        return string.Join(".", parts);
    }

    private static string? NamespaceName(BaseTypeDeclarationSyntax type) =>
        type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

    private static string? ContainingTypeName(BaseTypeDeclarationSyntax type) =>
        type.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;

    private static string Accessibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword))
            return "public";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword) && modifiers.Any(SyntaxKind.InternalKeyword))
            return "protected internal";
        if (modifiers.Any(SyntaxKind.PrivateKeyword) && modifiers.Any(SyntaxKind.ProtectedKeyword))
            return "private protected";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword))
            return "protected";
        if (modifiers.Any(SyntaxKind.InternalKeyword))
            return "internal";
        if (modifiers.Any(SyntaxKind.PrivateKeyword))
            return "private";
        return "internal";
    }

    private static bool IsPublicApi(string accessibility) =>
        accessibility is "public" or "protected" or "protected internal";

    private static string TypeSignature(BaseTypeDeclarationSyntax type)
    {
        var modifiers = type.Modifiers.ToString();
        var keyword = type switch
        {
            InterfaceDeclarationSyntax => "interface",
            StructDeclarationSyntax => "struct",
            RecordDeclarationSyntax r => r.ClassOrStructKeyword.ValueText.Length > 0 ? $"record {r.ClassOrStructKeyword.ValueText}" : "record",
            EnumDeclarationSyntax => "enum",
            ClassDeclarationSyntax => "class",
            _ => "type",
        };
        var typeParameters = (type as TypeDeclarationSyntax)?.TypeParameterList?.ToString() ?? string.Empty;
        var baseList = type.BaseList?.ToString() ?? string.Empty;
        var head = $"{modifiers} {keyword} {type.Identifier.ValueText}{typeParameters} {baseList}".Trim();
        while (head.Contains("  ", StringComparison.Ordinal))
            head = head.Replace("  ", " ", StringComparison.Ordinal);
        return head;
    }

    private static string FirstLine(string content)
    {
        var newline = content.IndexOfAny(['\r', '\n']);
        return (newline >= 0 ? content[..newline] : content).Trim();
    }

    // Cheap token estimate for the syntax stage: roughly four characters per token. Precise counting (via the
    // tokenizer) is applied at render time; the index only needs a stable ordering hint and a budget estimate.
    private static int EstimateTokens(string text) => (text.Length + 3) / 4;

    private static string HashEstimate(string text) =>
        new FileHashService().ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
///     The symbols and chunks extracted from one file by <see cref="SyntaxSymbolExtractor" />.
/// </summary>
/// <param name="Symbols">The extracted symbols.</param>
/// <param name="Chunks">The extracted chunks.</param>
public sealed record SyntaxExtractionResult(
    IReadOnlyList<SymbolRecord> Symbols,
    IReadOnlyList<ChunkRecord> Chunks)
{
    /// <summary>An empty result.</summary>
    public static SyntaxExtractionResult Empty { get; } = new([], []);
}
