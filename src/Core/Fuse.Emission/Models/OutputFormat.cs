namespace Fuse.Emission.Models;

/// <summary>
///     Supported fused output serialization formats.
/// </summary>
public enum OutputFormat
{
    /// <summary>XML file blocks (default).</summary>
    Xml,

    /// <summary>Markdown sections with fenced code blocks.</summary>
    Markdown,

    /// <summary>JSON lines with manifest and file entries.</summary>
    Json,
}
