using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuse.Cli.Configuration.McpInstall;

/// <summary>
///     MCP configuration used by clients whose local server command is a JSON array.
/// </summary>
internal sealed class LocalArrayMcpConfig
{
    /// <summary>
    ///     Gets or sets the MCP entries keyed by server name.
    /// </summary>
    [JsonPropertyName("mcp")]
    public Dictionary<string, JsonElement> Mcp { get; set; } = new();

    /// <summary>
    ///     Gets or sets top-level keys that the installer does not model.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; set; } = new();
}
