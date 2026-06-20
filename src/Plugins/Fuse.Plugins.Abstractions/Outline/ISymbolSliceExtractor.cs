namespace Fuse.Plugins.Abstractions.Outline;

/// <summary>
///     Reduces a source file to a single named member in full plus the signatures of everything else, so a
///     large file can be scoped to just the member a task is about (for example <c>OrderService.Charge</c> and
///     the shell of its type, not all of its other methods).
/// </summary>
/// <remarks>
///     This is the capability behind symbol-level scoping. A precise (semantic) implementation is required to
///     resolve member boundaries reliably; the regex tier does not provide one, so symbol-level scoping is part
///     of the opt-in precision tier.
/// </remarks>
public interface ISymbolSliceExtractor : ILanguageCapability
{
    /// <summary>
    ///     Returns a copy of the content in which only members named <paramref name="memberName" /> keep their
    ///     bodies and every other member is reduced to its signature.
    /// </summary>
    /// <param name="content">The source content to slice. Must not be <see langword="null" />.</param>
    /// <param name="memberName">The simple name of the member to keep in full (may match several overloads).</param>
    /// <returns>
    ///     The sliced content, or <see langword="null" /> when the content declares no member with that name,
    ///     so the caller can fall back to the whole file.
    /// </returns>
    string? ExtractSlice(string content, string memberName);
}
