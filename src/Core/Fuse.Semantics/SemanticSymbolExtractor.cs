using Fuse.Indexing;
using Microsoft.CodeAnalysis;

namespace Fuse.Semantics;

/// <summary>
///     Extracts symbols from a loaded project's compilation, with stable assembly-qualified ids and resolved
///     metadata (namespace, accessibility, signatures, public-API surface).
/// </summary>
/// <remarks>
///     This is the semantic counterpart to <see cref="SyntaxSymbolExtractor" />: it walks the compilation's
///     declared types and members rather than a single file's syntax, and keys each record on
///     <see cref="SymbolIdBuilder" /> rather than a source-position fallback. Only source-declared, non-compiler
///     symbols are emitted; property/event accessor methods are skipped.
/// </remarks>
public sealed class SemanticSymbolExtractor
{
    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType
            | SymbolDisplayMemberOptions.IncludeModifiers | SymbolDisplayMemberOptions.IncludeAccessibility,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    ///     Extracts symbol records for a loaded project.
    /// </summary>
    /// <param name="project">The loaded project and its compilation.</param>
    /// <param name="rootDirectory">The workspace root, used to make file paths relative.</param>
    /// <param name="cancellationToken">A token to cancel the extraction.</param>
    /// <returns>The extracted symbols, keyed to files by normalized relative path.</returns>
    public IReadOnlyList<SymbolRecord> Extract(LoadedProject project, string rootDirectory, CancellationToken cancellationToken)
    {
        var records = new List<SymbolRecord>();
        var assemblyName = project.AssemblyName;

        foreach (var type in EnumerateTypes(project.Compilation.Assembly.GlobalNamespace))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryAddSymbol(records, type, rootDirectory, assemblyName, project.FilePath);

            foreach (var member in type.GetMembers())
            {
                if (ShouldSkipMember(member))
                    continue;
                TryAddSymbol(records, member, rootDirectory, assemblyName, project.FilePath);
            }
        }

        return records;
    }

    private void TryAddSymbol(
        List<SymbolRecord> records,
        ISymbol symbol,
        string rootDirectory,
        string? assemblyName,
        string projectPath)
    {
        if (symbol.IsImplicitlyDeclared)
            return;

        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location?.SourceTree is null)
            return;

        var filePath = NormalizeRelative(rootDirectory, location.SourceTree.FilePath);
        var lineSpan = location.GetLineSpan();
        var containingType = symbol.ContainingType;

        records.Add(new SymbolRecord(
            SymbolId: SymbolIdBuilder.Build(symbol),
            FilePath: filePath,
            Kind: SymbolIdBuilder.KindTag(symbol),
            Name: symbol.Name,
            FullyQualifiedName: symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            MetadataName: symbol.MetadataName,
            ContainingType: containingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            Namespace: symbol.ContainingNamespace?.IsGlobalNamespace == true
                ? null
                : symbol.ContainingNamespace?.ToDisplayString(),
            AssemblyName: assemblyName,
            Accessibility: symbol.DeclaredAccessibility.ToString(),
            Signature: BuildSignature(symbol),
            StartLine: lineSpan.StartLinePosition.Line + 1,
            EndLine: lineSpan.EndLinePosition.Line + 1,
            IsPublicApi: IsPubliclyVisible(symbol),
            ProjectPath: projectPath));
    }

    private string BuildSignature(ISymbol symbol) =>
        symbol is INamedTypeSymbol
            ? symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            : symbol.ToDisplayString(SignatureFormat);

    private static bool ShouldSkipMember(ISymbol member) =>
        member is IMethodSymbol
        {
            MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet
                or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise
        };

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol root)
    {
        foreach (var member in root.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol ns:
                    foreach (var nested in EnumerateTypes(ns))
                        yield return nested;
                    break;
                case INamedTypeSymbol type:
                    foreach (var emitted in EnumerateTypeAndNested(type))
                        yield return emitted;
                    break;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypeAndNested(INamedTypeSymbol type)
    {
        yield return type;
        foreach (var nested in type.GetTypeMembers())
            foreach (var emitted in EnumerateTypeAndNested(nested))
                yield return emitted;
    }

    // Publicly visible when the symbol and every containing type are public or protected (reachable from
    // outside the assembly).
    private static bool IsPubliclyVisible(ISymbol symbol)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current is INamespaceSymbol)
                break;
            if (current.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal))
                return false;
        }

        return true;
    }

    private static string NormalizeRelative(string rootDirectory, string absolutePath)
    {
        var relative = Path.GetRelativePath(rootDirectory, absolutePath);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}
