using System.Text.Json;
using System.Text.Json.Serialization;
using Fuse.Cli.Configuration;
using Fuse.Cli.Configuration.McpInstall;
using Fuse.Cli.Mcp;

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
[JsonSerializable(typeof(RaceCandidateInput[]))]
internal partial class FuseCliJsonContext : JsonSerializerContext;
