namespace Fuse.Reduction.Markers;

/// <summary>
///     Generates structural semantic marker comments for source content.
/// </summary>
public interface ISemanticMarkerGenerator
{
    /// <summary>
    ///     Gets the file extension this generator handles, including the leading dot.
    /// </summary>
    string Extension { get; }

    /// <summary>
    ///     Generates semantic markers for types found in the content.
    /// </summary>
    /// <param name="content">Source content to analyze.</param>
    /// <returns>One marker per type declaration found.</returns>
    IReadOnlyList<SemanticMarker> GenerateMarkers(string content);
}
