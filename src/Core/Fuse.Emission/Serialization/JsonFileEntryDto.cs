namespace Fuse.Emission.Serialization;

/// <summary>
///     Single fused file entry in JSON output format.
/// </summary>
public sealed class JsonFileEntryDto
{
    /// <summary>
    ///     Discriminator for file records (<c>file</c>).
    /// </summary>
    public string Type { get; set; } = "file";

    /// <summary>
    ///     Relative path of the fused file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     Fused file content body.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    ///     Token count for the entry.
    /// </summary>
    public int Tokens { get; set; }

    /// <summary>
    ///     Original source file size in bytes when metadata is enabled.
    /// </summary>
    public long? Size { get; set; }

    /// <summary>
    ///     Last modification timestamp when metadata is enabled.
    /// </summary>
    public string? Modified { get; set; }

    /// <summary>
    ///     Dependency inclusion provenance labels when enabled.
    /// </summary>
    public string[]? Provenance { get; set; }
}
