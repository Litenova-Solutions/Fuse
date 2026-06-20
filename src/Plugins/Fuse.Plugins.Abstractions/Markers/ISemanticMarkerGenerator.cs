namespace Fuse.Plugins.Abstractions.Markers;

/// <summary>
///     Generates structural <see cref="SemanticMarker" /> annotations describing the types declared in
///     source content.
/// </summary>
/// <remarks>
///     Resolved by file extension through <see cref="CapabilityRegistry{TCapability}" /> using
///     <see cref="ILanguageCapability.SupportedExtensions" />. Implementations must be stateless to allow
///     concurrent generation across files.
/// </remarks>
public interface ISemanticMarkerGenerator : ILanguageCapability
{
    /// <summary>
    ///     Generates one semantic marker per type declaration found in the content.
    /// </summary>
    /// <param name="content">
    ///     Source content to analyze. Must not be <see langword="null" />; an empty string yields an empty list.
    /// </param>
    /// <returns>
    ///     One <see cref="SemanticMarker" /> per type declaration found, in declaration order. Empty when the
    ///     content declares no types.
    /// </returns>
    IReadOnlyList<SemanticMarker> GenerateMarkers(string content);
}
