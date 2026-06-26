using Fuse.Indexing;
using Microsoft.CodeAnalysis;

namespace Fuse.Semantics.Analyzers;

/// <summary>
///     Builds stable node ids and <see cref="NodeRecord" />s for the semantic graph. Node ids are the keys
///     edges reference, so they must be identical wherever the same logical node is produced by different
///     analyzers.
/// </summary>
public static class SemanticNodes
{
    /// <summary>
    ///     A display format producing a namespace-qualified name without the <c>global::</c> prefix, used in
    ///     node ids and display names.
    /// </summary>
    public static readonly SymbolDisplayFormat NodeNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    /// <summary>Returns the node id for a type.</summary>
    /// <param name="type">The type symbol.</param>
    /// <returns>A node id of the form <c>type:{fully-qualified-name}</c>.</returns>
    public static string TypeId(INamedTypeSymbol type) => "type:" + type.ToDisplayString(NodeNameFormat);

    /// <summary>Returns the node id for a method.</summary>
    /// <param name="method">The method symbol.</param>
    /// <returns>A node id of the form <c>method:{type}.{name}</c>.</returns>
    public static string MethodId(IMethodSymbol method)
    {
        var container = method.ContainingType is { } type ? type.ToDisplayString(NodeNameFormat) + "." : string.Empty;
        return $"method:{container}{method.Name}";
    }

    /// <summary>Returns the node id for a route.</summary>
    /// <param name="httpMethod">The HTTP method.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <returns>A node id of the form <c>route:{METHOD}:{pattern}</c>.</returns>
    public static string RouteId(string httpMethod, string pattern) => $"route:{httpMethod}:{pattern}";

    /// <summary>Returns the node id for a service name.</summary>
    /// <param name="serviceName">The service type name.</param>
    /// <returns>A node id of the form <c>service:{name}</c>.</returns>
    public static string ServiceId(string serviceName) => "service:" + serviceName;

    /// <summary>Returns the node id for a configuration section.</summary>
    /// <param name="section">The configuration section name.</param>
    /// <returns>A node id of the form <c>config:{section}</c>.</returns>
    public static string ConfigId(string section) => "config:" + section;

    /// <summary>
    ///     Builds a node record for a type symbol.
    /// </summary>
    /// <param name="type">The type symbol.</param>
    /// <param name="rootDirectory">The workspace root, used to make the file path relative.</param>
    /// <returns>A node record keyed by <see cref="TypeId(INamedTypeSymbol)" />.</returns>
    public static NodeRecord TypeNode(INamedTypeSymbol type, string rootDirectory)
    {
        var location = type.Locations.FirstOrDefault(l => l.IsInSource);
        var lineSpan = location?.GetLineSpan();
        return new NodeRecord(
            NodeId: TypeId(type),
            Kind: SymbolIdBuilder.KindTag(type),
            DisplayName: type.Name,
            StableKey: type.ToDisplayString(NodeNameFormat),
            FilePath: NormalizeRelative(rootDirectory, location?.SourceTree?.FilePath),
            SymbolId: SymbolIdBuilder.Build(type),
            StartLine: lineSpan?.StartLinePosition.Line + 1,
            EndLine: lineSpan?.EndLinePosition.Line + 1,
            Signature: type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }

    /// <summary>
    ///     Builds a node record for a method symbol.
    /// </summary>
    /// <param name="method">The method symbol.</param>
    /// <param name="rootDirectory">The workspace root, used to make the file path relative.</param>
    /// <returns>A node record keyed by <see cref="MethodId(IMethodSymbol)" />.</returns>
    public static NodeRecord MethodNode(IMethodSymbol method, string rootDirectory)
    {
        var location = method.Locations.FirstOrDefault(l => l.IsInSource);
        var lineSpan = location?.GetLineSpan();
        return new NodeRecord(
            NodeId: MethodId(method),
            Kind: "method",
            DisplayName: method.Name,
            StableKey: method.ToDisplayString(NodeNameFormat),
            FilePath: NormalizeRelative(rootDirectory, location?.SourceTree?.FilePath),
            SymbolId: SymbolIdBuilder.Build(method),
            StartLine: lineSpan?.StartLinePosition.Line + 1,
            EndLine: lineSpan?.EndLinePosition.Line + 1,
            Signature: method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }

    /// <summary>
    ///     Returns whether a type is declared in the project's own source (not an external assembly).
    /// </summary>
    /// <param name="type">The type symbol.</param>
    /// <param name="compilation">The project compilation.</param>
    /// <returns><c>true</c> when the type belongs to the compilation's source assembly.</returns>
    public static bool IsInSource(INamedTypeSymbol type, Compilation compilation) =>
        SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
        && type.Locations.Any(l => l.IsInSource);

    /// <summary>
    ///     Enumerates every named type declared in a compilation, including nested types.
    /// </summary>
    /// <param name="compilation">The compilation to walk.</param>
    /// <returns>The declared named types.</returns>
    public static IEnumerable<INamedTypeSymbol> EnumerateTypes(Compilation compilation) =>
        EnumerateTypes(compilation.Assembly.GlobalNamespace);

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

    private static string? NormalizeRelative(string rootDirectory, string? absolutePath) =>
        string.IsNullOrEmpty(absolutePath)
            ? null
            : Path.GetRelativePath(rootDirectory, absolutePath).Replace(Path.DirectorySeparatorChar, '/');
}
