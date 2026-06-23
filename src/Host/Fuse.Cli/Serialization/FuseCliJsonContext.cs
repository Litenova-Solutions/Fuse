using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Cli.Configuration;
using Fuse.Cli.Configuration.McpInstall;

namespace Fuse.Cli.Serialization;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    WriteIndented = true)]
[JsonSerializable(typeof(FuseConfig))]
[JsonSerializable(typeof(ClaudeMcpConfig))]
[JsonSerializable(typeof(CursorMcpConfig))]
[JsonSerializable(typeof(CopilotMcpConfig))]
internal partial class FuseCliJsonContext : JsonSerializerContext;
