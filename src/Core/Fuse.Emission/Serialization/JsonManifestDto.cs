namespace Fuse.Emission.Serialization;

/// <summary>
///     Root manifest object for JSON output format.
/// </summary>
public sealed class JsonManifestDto
{
    /// <summary>
    ///     Discriminator for manifest records (<c>manifest</c>).
    /// </summary>
    public string Type { get; set; } = "manifest";

    /// <summary>
    ///     Per-file manifest entries sorted by path.
    /// </summary>
    public JsonManifestFileDto[] Files { get; set; } = [];

    /// <summary>
    ///     Optional cross-cutting pattern summaries.
    /// </summary>
    public JsonPatternDto[]? Patterns { get; set; }

    /// <summary>
    ///     Git availability marker when stats cannot be collected.
    /// </summary>
    public string? Git { get; set; }
}
