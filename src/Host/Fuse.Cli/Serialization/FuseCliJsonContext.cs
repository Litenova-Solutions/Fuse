using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Cli.Configuration;

namespace Fuse.Cli.Serialization;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(FuseConfig))]
internal partial class FuseCliJsonContext : JsonSerializerContext;
