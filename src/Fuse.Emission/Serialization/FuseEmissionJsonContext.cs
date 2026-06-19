using System.Text.Json;
using System.Text.Json.Serialization;

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

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonManifestDto))]
[JsonSerializable(typeof(JsonFileEntryDto))]
internal partial class FuseEmissionJsonContext : JsonSerializerContext;
