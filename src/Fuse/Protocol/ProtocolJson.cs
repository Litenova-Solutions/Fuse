using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuse.Protocol;

/// <summary>Source-generated serialization for the engine pipe protocol.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(EngineRequest))]
[JsonSerializable(typeof(EngineResponse))]
internal sealed partial class ProtocolJson : JsonSerializerContext
{
    private static readonly string BuildIdProperty = JsonNamingPolicy.CamelCase.ConvertName(nameof(Fuse.Protocol.EngineRequest.BuildId));

    public static string Serialize(EngineRequest request) => JsonSerializer.Serialize(request, Default.EngineRequest);

    public static string Serialize(EngineResponse response) => JsonSerializer.Serialize(response, Default.EngineResponse);

    /// <summary>
    ///     The build id a request line carries, read without reading the request's case, so an engine can answer
    ///     <see cref="EngineResponse.Restart"/> to a request whose case its own build does not have. Null when the line is a
    ///     JSON object without a build id, as a request from a build that named it differently is.
    /// </summary>
    /// <exception cref="JsonException">The line is not a JSON object.</exception>
    public static string? ReadBuildId(string line)
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("an engine request is a JSON object");
        return document.RootElement.TryGetProperty(BuildIdProperty, out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    }

    /// <summary>Reads a request of this build. Its case property has to come first, as <see cref="Serialize(EngineRequest)"/> writes it.</summary>
    public static EngineRequest? ReadRequest(string line) => JsonSerializer.Deserialize(line, Default.EngineRequest);

    /// <summary>Reads a response. Its case property has to come first, as <see cref="Serialize(EngineResponse)"/> writes it.</summary>
    public static EngineResponse? ReadResponse(string line) => JsonSerializer.Deserialize(line, Default.EngineResponse);
}
