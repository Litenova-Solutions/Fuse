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
    public static string Serialize(EngineRequest request) => JsonSerializer.Serialize(request, Default.EngineRequest);

    public static string Serialize(EngineResponse response) => JsonSerializer.Serialize(response, Default.EngineResponse);

    public static EngineRequest? ReadRequest(string line) => JsonSerializer.Deserialize(line, Default.EngineRequest);

    public static EngineResponse? ReadResponse(string line) => JsonSerializer.Deserialize(line, Default.EngineResponse);
}
