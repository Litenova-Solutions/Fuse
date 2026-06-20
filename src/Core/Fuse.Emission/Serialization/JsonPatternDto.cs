namespace Fuse.Emission.Serialization;

/// <summary>
///     Pattern summary entry in a JSON manifest.
/// </summary>
public sealed class JsonPatternDto
{
    /// <summary>
    ///     Display name of the detected pattern.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Human-readable pattern summary text.
    /// </summary>
    public string Summary { get; set; } = string.Empty;
}
