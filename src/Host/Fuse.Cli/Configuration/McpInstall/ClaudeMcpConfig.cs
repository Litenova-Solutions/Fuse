using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuse.Cli.Configuration.McpInstall;

/// <summary>
///     Claude Code project-level MCP configuration (<c>.mcp.json</c>).
/// </summary>
internal sealed class ClaudeMcpConfig
{
    /// <summary>
    ///     Gets or sets the MCP server entries keyed by server name.
    /// </summary>
    [JsonPropertyName("mcpServers")]
    public Dictionary<string, ClaudeMcpServer> McpServers { get; set; } = new();

    /// <summary>
    ///     Top-level keys the installer does not model, preserved verbatim across a read-modify-write.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; set; } = new();
}
