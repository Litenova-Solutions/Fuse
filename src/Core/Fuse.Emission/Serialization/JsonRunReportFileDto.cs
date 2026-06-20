namespace Fuse.Emission.Serialization;

/// <summary>
///     Per-file entry in a JSON run report.
/// </summary>
public sealed class JsonRunReportFileDto
{
    /// <summary>
    ///     Relative path of the emitted file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     Token count for the file.
    /// </summary>
    public long Tokens { get; set; }
}
