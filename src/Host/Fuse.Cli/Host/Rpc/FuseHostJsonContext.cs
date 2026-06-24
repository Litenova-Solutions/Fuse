using System.Text.Json.Serialization;

namespace Fuse.Cli.Rpc;

/// <summary>
///     The source-generated JSON serialization context for every host RPC DTO. The host transport is configured
///     to use this resolver so all UI-facing JSON is reflection-free, satisfying the project invariant that JSON
///     goes through a <see cref="JsonSerializerContext" /> only. <c>protocol.ts</c> in the extension mirrors
///     these shapes and is pinned by a contract test.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FuseHostHandshake))]
[JsonSerializable(typeof(FuseHostStats))]
[JsonSerializable(typeof(GraphDto))]
[JsonSerializable(typeof(GraphNodeDto))]
[JsonSerializable(typeof(GraphEdgeDto))]
[JsonSerializable(typeof(IndexResultDto))]
public sealed partial class FuseHostJsonContext : JsonSerializerContext;
