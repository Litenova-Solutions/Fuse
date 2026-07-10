using Fuse.Indexing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Fuse.Semantics;

/// <summary>
///     Extracts the public and protected API surface of a compiled assembly (a NuGet package's <c>lib</c> or
///     <c>ref</c> DLL) as <see cref="SymbolRecord" /> values, so the NuGet upgrade oracle (F3) can diff two
///     package versions with the same <c>PublicApiDelta</c> comparison T2 uses over source. Metadata-only: the
///     assembly is loaded as a <see cref="MetadataReference" /> into a throwaway compilation and its public
///     symbols are walked; no source and no execution.
/// </summary>
/// <remarks>
///     The fully qualified name includes the method signature (parameter types), so an overload set and a
///     signature change are distinguished by the by-name diff. Only public and protected members participate,
///     matching T2's conservative surface contract; a member whose type does not resolve (a transitive reference
///     not supplied) still yields a stable display string, which is enough to diff name and shape.
/// </remarks>
public static class MetadataSurfaceExtractor
{
    private static readonly SymbolDisplayFormat FqnFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeType | SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeContainingType,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>
    ///     Extracts the public and protected surface of the assembly at the given path.
    /// </summary>
    /// <param name="assemblyPath">The absolute path to the assembly DLL.</param>
    /// <returns>The public and protected type and member symbols; empty when the assembly cannot be loaded.</returns>
    public static IReadOnlyList<SymbolRecord> Extract(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
            return [];

        MetadataReference reference;
        try { reference = MetadataReference.CreateFromFile(assemblyPath); }
        catch (Exception) { return []; }

        var compilation = CSharpCompilation.Create("surface-probe", references: [reference]);
        if (compilation.GetAssemblyOrModuleSymbol(reference) is not IAssemblySymbol assembly)
            return [];

        var records = new List<SymbolRecord>();
        foreach (var type in EnumerateTypes(assembly.GlobalNamespace))
        {
            if (!IsVisible(type.DeclaredAccessibility))
                continue;

            var typeFqn = type.ToDisplayString(FqnFormat);
            records.Add(new SymbolRecord(
                SymbolId: $"meta:{typeFqn}",
                FilePath: assemblyPath,
                Kind: type.TypeKind.ToString().ToLowerInvariant(),
                Name: type.Name,
                FullyQualifiedName: typeFqn,
                Accessibility: type.DeclaredAccessibility.ToString(),
                Signature: type.ToDisplayString(SignatureFormat),
                IsPublicApi: true));

            foreach (var member in type.GetMembers())
            {
                if (!IsVisible(member.DeclaredAccessibility) || member.IsImplicitlyDeclared)
                    continue;
                if (member is not (IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor } or IPropertySymbol or IFieldSymbol or IEventSymbol))
                    continue;

                var memberFqn = member.ToDisplayString(FqnFormat);
                records.Add(new SymbolRecord(
                    SymbolId: $"meta:{memberFqn}",
                    FilePath: assemblyPath,
                    Kind: member.Kind.ToString().ToLowerInvariant(),
                    Name: member.Name,
                    FullyQualifiedName: memberFqn,
                    ContainingType: typeFqn,
                    Accessibility: member.DeclaredAccessibility.ToString(),
                    Signature: member.ToDisplayString(SignatureFormat),
                    IsPublicApi: true));
            }
        }

        return records;
    }

    private static bool IsVisible(Accessibility accessibility) =>
        accessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in NestedTypes(type))
                yield return nested;
        }

        foreach (var child in ns.GetNamespaceMembers())
            foreach (var type in EnumerateTypes(child))
                yield return type;
    }

    private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            if (!IsVisible(nested.DeclaredAccessibility))
                continue;
            yield return nested;
            foreach (var deeper in NestedTypes(nested))
                yield return deeper;
        }
    }
}
