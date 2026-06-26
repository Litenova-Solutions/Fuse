using System.IO.Hashing;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Fuse.Semantics;

/// <summary>
///     Builds stable, collision-resistant identifiers for Roslyn symbols that are identical across indexing
///     runs and independent of source position.
/// </summary>
/// <remarks>
///     The identity is derived from the containing assembly and the symbol's documentation comment id, which
///     already encodes namespace, containing-type chain, member name, generic arity, and method parameter
///     types. The id format is <c>symbol:{assembly}:{kind}:{hash}</c>, hashed so the key stays bounded for
///     generic methods with long parameter lists. Source-only symbols that lack a documentation id fall back
///     to the fully qualified display string.
/// </remarks>
public static class SymbolIdBuilder
{
    /// <summary>
    ///     Builds the stable id for a symbol.
    /// </summary>
    /// <param name="symbol">The symbol to identify.</param>
    /// <returns>A stable id of the form <c>symbol:{assembly}:{kind}:{hash}</c>.</returns>
    public static string Build(ISymbol symbol)
    {
        var assembly = symbol.ContainingAssembly?.Identity.Name ?? "source";
        var descriptor = symbol.GetDocumentationCommentId()
            ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes($"{assembly}|{descriptor}")).ToString("x16");
        return $"symbol:{assembly}:{KindTag(symbol)}:{hash}";
    }

    /// <summary>
    ///     Returns the kind tag used in symbol ids and stored as the symbol kind.
    /// </summary>
    /// <param name="symbol">The symbol.</param>
    /// <returns>A lowercase kind tag (for example <c>class</c>, <c>interface</c>, <c>method</c>, <c>constructor</c>).</returns>
    public static string KindTag(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct => "struct",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => type.IsRecord ? "record" : "class",
        },
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };
}
