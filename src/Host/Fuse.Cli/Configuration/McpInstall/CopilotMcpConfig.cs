using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuse.Cli.Configuration.McpInstall;

/// <summary>
///     GitHub Copilot in VS Code MCP configuration (<c>.vscode/mcp.json</c>).
/// </summary>
internal sealed class CopilotMcpConfig
{
    /// <summary>
    ///     Gets or sets the MCP server entries keyed by server name.
    /// </summary>
    [JsonPropertyName("servers")]
    public Dictionary<string, CopilotMcpServer> Servers { get; set; } = new();

    /// <summary>
    ///     Top-level keys the installer does not model (for example <c>inputs</c>), preserved verbatim across a
    ///     read-modify-write.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; set; } = new();
}
