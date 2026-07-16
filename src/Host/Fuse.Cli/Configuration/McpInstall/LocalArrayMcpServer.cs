using System.Text.Json.Serialization;

namespace Fuse.Cli.Configuration.McpInstall;

/// <summary>
///     One local MCP server entry whose command includes the executable and arguments.
/// </summary>
internal sealed class LocalArrayMcpServer
{
    /// <summary>
    ///     Gets or sets the local process transport type.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "local";

    /// <summary>
    ///     Gets or sets the executable followed by its arguments.
    /// </summary>
    [JsonPropertyName("command")]
    public string[] Command { get; set; } = [];

    /// <summary>
    ///     Gets or sets whether the server is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}
