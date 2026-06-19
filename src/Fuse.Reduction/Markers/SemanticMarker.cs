namespace Fuse.Reduction.Markers;

/// <summary>
///     A structural annotation for a single type declaration.
/// </summary>
public sealed record SemanticMarker(
    string TypeName,
    string Kind,
    IReadOnlyList<string> Implements,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> ConstructorParameterTypes)
{
    /// <summary>
    ///     Renders the marker as a single-line XML comment.
    /// </summary>
    public string ToComment()
    {
        return $"<!-- fuse:type {TypeName} | kind:{Kind} | implements:{FormatList(Implements)} | depends-on:{FormatList(DependsOn)} | constructors:{FormatList(ConstructorParameterTypes)} -->";
    }

    private static string FormatList(IReadOnlyList<string> items) =>
        items.Count == 0 ? "none" : string.Join(",", items);
}
