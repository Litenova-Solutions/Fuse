namespace Fuse.Emission.Serialization;

/// <summary>
///     JSON manifest file entry emitted at the start of JSON output.
/// </summary>
public sealed class JsonManifestFileDto
{
    /// <summary>
    ///     Relative path of the emitted file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     Token count for the file entry.
    /// </summary>
    public long Tokens { get; set; }

    /// <summary>
    ///     Git commit count when git stats are available.
    /// </summary>
    public int? Commits { get; set; }

    /// <summary>
    ///     Last git modification date (<c>yyyy-MM-dd</c>) when available.
    /// </summary>
    public string? LastModified { get; set; }
}
