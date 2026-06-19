using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuse.Cli.Configuration;

/// <summary>
///     Fuse project configuration loaded from <c>fuse.json</c> or <c>.fuserc</c>.
/// </summary>
public sealed class FuseConfig
{
    /// <summary>Source directory to fuse.</summary>
    public string? Directory { get; set; }

    /// <summary>Output directory for fused files.</summary>
    public string? Output { get; set; }

    /// <summary>Custom output filename without extension.</summary>
    public string? Name { get; set; }

    /// <summary>Output format: xml, markdown, or json.</summary>
    public string? Format { get; set; }

    /// <summary>Tokenizer model or encoding name.</summary>
    public string? Tokenizer { get; set; }

    /// <summary>Disable manifest header when true.</summary>
    public bool? NoManifest { get; set; }

    /// <summary>Enable inclusion provenance annotations.</summary>
    public bool? Provenance { get; set; }

    /// <summary>Include git stats in manifest.</summary>
    public bool? GitStats { get; set; }

    /// <summary>Hard token limit.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Split threshold in tokens.</summary>
    public int? SplitTokens { get; set; }

    /// <summary>Search recursively.</summary>
    public bool? Recursive { get; set; }

    /// <summary>Include file metadata in output.</summary>
    public bool? IncludeMetadata { get; set; }
}
