using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuse.Emission.Serialization;

/// <summary>
///     Source-generated <see cref="JsonSerializerContext" /> for emission JSON output.
/// </summary>
/// <remarks>
///     Serializes <see cref="JsonManifestDto" /> and <see cref="JsonFileEntryDto" /> with camelCase
///     property names, omitting null values. Used for AOT-safe JSON emission.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonManifestDto))]
[JsonSerializable(typeof(JsonFileEntryDto))]
[JsonSerializable(typeof(JsonRunReportDto))]
[JsonSerializable(typeof(JsonVerifyReportDto))]
[JsonSerializable(typeof(JsonTocDto))]
internal partial class FuseEmissionJsonContext : JsonSerializerContext;
