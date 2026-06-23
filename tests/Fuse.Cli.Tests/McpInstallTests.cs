using System.Text.Json;
using Fuse.Cli.Configuration.McpInstall;
using Fuse.Cli.Serialization;
using Fuse.Cli.Services;

namespace Fuse.Cli.Tests;

public sealed class McpInstallTests
{
    [Fact]
    public async Task InstallAsync_ProjectScope_WritesClaudeCursorAndCopilotConfigs()
    {
        var root = CreateTempDirectory();
        var console = new RecordingConsoleUI();
        var service = new McpInstallService();

        var configured = await service.InstallAsync(
            [McpInstallClient.Claude, McpInstallClient.Cursor, McpInstallClient.Copilot],
            McpInstallScope.Project,
            root,
            "/usr/local/bin/fuse",
            console,
            CancellationToken.None);

        Assert.Equal(3, configured);
        Assert.True(File.Exists(Path.Combine(root, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(root, ".cursor", "mcp.json")));
        Assert.True(File.Exists(Path.Combine(root, ".vscode", "mcp.json")));

        var claude = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(Path.Combine(root, ".mcp.json")),
            FuseCliJsonContext.Default.ClaudeMcpConfig);
        Assert.NotNull(claude);
        Assert.Equal("/usr/local/bin/fuse", claude!.McpServers["fuse"].Command);
        Assert.Equal(["mcp", "serve"], claude.McpServers["fuse"].Args);
        Assert.Equal("stdio", claude.McpServers["fuse"].Type);

        var cursor = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(Path.Combine(root, ".cursor", "mcp.json")),
            FuseCliJsonContext.Default.CursorMcpConfig);
        Assert.NotNull(cursor);
        Assert.Equal("/usr/local/bin/fuse", cursor!.McpServers["fuse"].Command);

        var copilot = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(Path.Combine(root, ".vscode", "mcp.json")),
            FuseCliJsonContext.Default.CopilotMcpConfig);
        Assert.NotNull(copilot);
        Assert.Equal("/usr/local/bin/fuse", copilot!.Servers["fuse"].Command);
        Assert.Equal("stdio", copilot.Servers["fuse"].Type);
    }

    [Fact]
    public async Task InstallAsync_MergesExistingServers()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, ".cursor"));
        await File.WriteAllTextAsync(
            Path.Combine(root, ".cursor", "mcp.json"),
            """
            {
              "mcpServers": {
                "other": {
                  "command": "other-tool",
                  "args": ["run"]
                }
              }
            }
            """);

        var service = new McpInstallService();
        var configured = await service.InstallAsync(
            [McpInstallClient.Cursor],
            McpInstallScope.Project,
            root,
            "fuse",
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.Equal(1, configured);

        var cursor = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(Path.Combine(root, ".cursor", "mcp.json")),
            FuseCliJsonContext.Default.CursorMcpConfig);
        Assert.NotNull(cursor);
        Assert.Equal(2, cursor!.McpServers.Count);
        Assert.Equal("other-tool", cursor.McpServers["other"].Command);
        Assert.Equal("fuse", cursor.McpServers["fuse"].Command);
    }

    [Fact]
    public void GetConfigPath_UserScope_UsesUserProfileDirectory()
    {
        var projectRoot = CreateTempDirectory();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(
            Path.Combine(profile, ".cursor", "mcp.json"),
            McpInstallService.GetConfigPath(McpInstallClient.Cursor, McpInstallScope.User, projectRoot));

        // VS Code reads user-level MCP config from its profile directory (Code/User), not ~/.vscode.
        var copilotUser = McpInstallService.GetConfigPath(McpInstallClient.Copilot, McpInstallScope.User, projectRoot);
        Assert.EndsWith(Path.Combine("Code", "User", "mcp.json"), copilotUser);
        Assert.DoesNotContain(Path.Combine(".vscode", "mcp.json"), copilotUser);
    }

    [Fact]
    public void GetConfigPath_ClaudeUserScope_IsNotSupported()
    {
        // Claude user scope goes through the Claude CLI, so there is no file path to return.
        Assert.Throws<NotSupportedException>(() =>
            McpInstallService.GetConfigPath(McpInstallClient.Claude, McpInstallScope.User, CreateTempDirectory()));
    }

    [Fact]
    public async Task InstallAsync_PreservesUnmodeledFieldsOfOtherServers()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, ".vscode"));
        await File.WriteAllTextAsync(
            Path.Combine(root, ".vscode", "mcp.json"),
            """
            {
              "inputs": [
                { "id": "token", "type": "promptString" }
              ],
              "servers": {
                "other": {
                  "type": "stdio",
                  "command": "other-tool",
                  "args": ["run"],
                  "env": { "API_KEY": "${input:token}" }
                }
              }
            }
            """);

        var service = new McpInstallService();
        var configured = await service.InstallAsync(
            [McpInstallClient.Copilot],
            McpInstallScope.Project,
            root,
            "fuse",
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.Equal(1, configured);

        using var written = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(root, ".vscode", "mcp.json")));
        var rootElement = written.RootElement;

        // Fuse was added.
        Assert.True(rootElement.GetProperty("servers").TryGetProperty("fuse", out _));

        // The unrelated server keeps its env block...
        var other = rootElement.GetProperty("servers").GetProperty("other");
        Assert.Equal("${input:token}", other.GetProperty("env").GetProperty("API_KEY").GetString());

        // ...and the top-level inputs array survives.
        Assert.Equal(1, rootElement.GetProperty("inputs").GetArrayLength());
        Assert.Equal("token", rootElement.GetProperty("inputs")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void GetConfigPath_ProjectScope_UsesProjectDirectory()
    {
        var projectRoot = CreateTempDirectory();

        Assert.Equal(
            Path.Combine(projectRoot, ".mcp.json"),
            McpInstallService.GetConfigPath(McpInstallClient.Claude, McpInstallScope.Project, projectRoot));
        Assert.Equal(
            Path.Combine(projectRoot, ".cursor", "mcp.json"),
            McpInstallService.GetConfigPath(McpInstallClient.Cursor, McpInstallScope.Project, projectRoot));
    }

    [Fact]
    public void ResolveFuseCommand_UsesProcessPathWhenAvailable()
    {
        var path = McpInstallService.ResolveFuseCommand();
        Assert.False(string.IsNullOrWhiteSpace(path));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fuse-install-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingConsoleUI : IConsoleUI
    {
        public void WriteSuccess(string message)
        {
        }

        public void WriteError(string message)
        {
        }

        public void WriteStep(string message)
        {
        }

        public void WriteResult(string message)
        {
        }
    }
}
