namespace Fuse.Plugins.Abstractions.Markers;

/// <summary>
///     A structural annotation for a single type declaration, surfaced to consumers as an inline
///     <c>&lt;!-- fuse:type ... --&gt;</c> comment.
/// </summary>
/// <param name="TypeName">Simple name of the declared type.</param>
/// <param name="Kind">Declaration kind, such as <c>class</c>, <c>interface</c>, <c>record</c>, or <c>struct</c>.</param>
/// <param name="Implements">Base type and interface names the type derives from or implements.</param>
/// <param name="DependsOn">Referenced type names that the type depends on.</param>
/// <param name="ConstructorParameterTypes">Parameter type names of the type's primary or first constructor.</param>
public sealed record SemanticMarker(
    string TypeName,
    string Kind,
    IReadOnlyList<string> Implements,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> ConstructorParameterTypes)
{
    /// <summary>
    ///     Renders the marker as a single-line XML comment suitable for prepending to a file entry.
    /// </summary>
    /// <returns>
    ///     A <c>&lt;!-- fuse:type ... --&gt;</c> comment. Empty list fields are rendered as the literal <c>none</c>.
    /// </returns>
    public string ToComment()
    {
        return $"<!-- fuse:type {TypeName} | kind:{Kind} | implements:{FormatList(Implements)} | depends-on:{FormatList(DependsOn)} | constructors:{FormatList(ConstructorParameterTypes)} -->";
    }

    private static string FormatList(IReadOnlyList<string> items) =>
        items.Count == 0 ? "none" : string.Join(",", items);
}
