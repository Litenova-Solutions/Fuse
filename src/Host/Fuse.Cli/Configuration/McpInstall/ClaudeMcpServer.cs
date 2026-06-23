using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fuse.Cli.Configuration.McpInstall;

/// <summary>
///     One stdio MCP server entry in Claude Code configuration.
/// </summary>
internal sealed class ClaudeMcpServer
{
    /// <summary>
    ///     Gets or sets the transport type (always <c>stdio</c> for Fuse).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "stdio";

    /// <summary>
    ///     Gets or sets the executable that the client launches.
    /// </summary>
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the arguments passed to <see cref="Command" />.
    /// </summary>
    [JsonPropertyName("args")]
    public string[] Args { get; set; } = [];

    /// <summary>
    ///     Per-server keys the installer does not model (for example <c>env</c> or <c>cwd</c>), preserved verbatim so
    ///     a co-located server's settings are never dropped when Fuse is added.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement> AdditionalProperties { get; set; } = new();
}
