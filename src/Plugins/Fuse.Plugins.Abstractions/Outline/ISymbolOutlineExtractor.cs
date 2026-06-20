namespace Fuse.Plugins.Abstractions.Outline;

/// <summary>
///     Extracts a structural outline (declared types and their members) from a source file, used to build a
///     cheap table-of-contents view that lets an agent see what a file contains before paying to read it.
/// </summary>
/// <remarks>
///     This capability is distinct from <see cref="Skeleton.ISkeletonExtractor" />: a skeleton renders
///     signature text suitable for inclusion in fused output, whereas an outline returns structured names for a
///     navigation index. A regex implementation is best-effort and may miss members in code with heavy
///     conditional compilation; a semantic implementation registered later overrides it by extension.
/// </remarks>
public interface ISymbolOutlineExtractor : ILanguageCapability
{
    /// <summary>
    ///     Extracts the declared types and their members from the supplied content.
    /// </summary>
    /// <param name="content">
    ///     Source content to scan. Must not be <see langword="null" />; an empty string yields an empty list.
    /// </param>
    /// <returns>
    ///     One <see cref="OutlineSymbol" /> per declared type, in declaration order. Empty when the content
    ///     declares no types.
    /// </returns>
    IReadOnlyList<OutlineSymbol> ExtractOutline(string content);
}
