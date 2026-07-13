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
            writeRules: false,
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
            writeRules: false,
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
            writeRules: false,
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

    [Fact]
    public async Task InstallAsync_WriteRules_ProjectScope_WritesRuleFilesForAllClients()
    {
        var root = CreateTempDirectory();
        var service = new McpInstallService();

        await service.InstallAsync(
            [McpInstallClient.Claude, McpInstallClient.Cursor, McpInstallClient.Copilot],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: true,
            new RecordingConsoleUI(),
            CancellationToken.None);

        var claude = await File.ReadAllTextAsync(Path.Combine(root, "CLAUDE.md"));
        Assert.Contains("fuse_find", claude);
        Assert.Contains("<!-- fuse:begin", claude);

        var cursor = await File.ReadAllTextAsync(Path.Combine(root, ".cursor", "rules", "fuse.mdc"));
        Assert.Contains("alwaysApply: true", cursor);
        Assert.Contains("fuse_workspace", cursor);

        var copilot = await File.ReadAllTextAsync(Path.Combine(root, ".github", "copilot-instructions.md"));
        Assert.Contains("fuse_review", copilot);

        var gitIgnore = await File.ReadAllTextAsync(Path.Combine(root, ".gitignore"));
        Assert.Contains(".fuse/", gitIgnore);
    }

    [Fact]
    public async Task InstallAsync_WriteRules_ProjectScope_DoesNotDuplicateGitIgnoreEntry()
    {
        var root = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".fuse/\n");

        var service = new McpInstallService();
        await service.InstallAsync(
            [McpInstallClient.Cursor],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: true,
            new RecordingConsoleUI(),
            CancellationToken.None);

        var gitIgnore = await File.ReadAllTextAsync(Path.Combine(root, ".gitignore"));
        Assert.Equal(".fuse/\n", gitIgnore);
    }

    [Fact]
    public async Task InstallAsync_ProjectScopeWithoutRules_DoesNotWriteGitIgnore()
    {
        var root = CreateTempDirectory();
        var service = new McpInstallService();

        await service.InstallAsync(
            [McpInstallClient.Cursor],
            McpInstallScope.Project,
            root,
            "fuse",
            writeRules: false,
            new RecordingConsoleUI(),
            CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(root, ".gitignore")));
    }

    [Fact]
    public async Task InstallAsync_WriteRules_IsIdempotentAndPreservesSurroundingContent()
    {
        var root = CreateTempDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(root, "CLAUDE.md"),
            "# My project rules\n\nKeep this paragraph.\n");

        var service = new McpInstallService();

        // Run twice; the managed block must appear exactly once and the user's content must survive.
        for (var i = 0; i < 2; i++)
            await service.InstallAsync(
                [McpInstallClient.Claude],
                McpInstallScope.Project,
                root,
                "fuse",
                writeRules: true,
                new RecordingConsoleUI(),
                CancellationToken.None);

        var claude = await File.ReadAllTextAsync(Path.Combine(root, "CLAUDE.md"));
        Assert.Contains("Keep this paragraph.", claude);
        Assert.Equal("# My project rules", claude.Split('\n')[0]);
        var occurrences = claude.Split("<!-- fuse:begin").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task InstallAsync_WriteRules_UserScope_SkipsCursorAndCopilot()
    {
        var root = CreateTempDirectory();
        var service = new McpInstallService();

        await service.InstallAsync(
            [McpInstallClient.Cursor, McpInstallClient.Copilot],
            McpInstallScope.User,
            root,
            "fuse",
            writeRules: true,
            new RecordingConsoleUI(),
            CancellationToken.None);

        // User scope has no Cursor/Copilot rules file, so nothing is written under the project root.
        Assert.False(File.Exists(Path.Combine(root, ".cursor", "rules", "fuse.mdc")));
        Assert.False(File.Exists(Path.Combine(root, ".github", "copilot-instructions.md")));
    }

    [Fact]
    public void Install_DoesNotRequireCommandOption()
    {
        // Regression: the optional --command option was inferred as required by the command framework, so
        // `fuse mcp install` failed with "Option '--command' is required" unless a value was passed. Parse only
        // (no execution) so this never writes real client config.
        var result = DotMake.CommandLine.Cli.Parse<Fuse.Cli.FuseCliCommand>(["mcp", "install", "--scope", "user"]);

        Assert.DoesNotContain(
            result.ParseResult.Errors,
            error => error.Message.Contains("command", StringComparison.OrdinalIgnoreCase));
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
