using Fuse.Cli.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Fuse.Cli.Tests.Mcp;

/// <summary>
///     End-to-end tests that spawn <c>fuse mcp serve</c> as a stdio subprocess and drive the MCP client protocol.
/// </summary>
public sealed class McpServeIntegrationTests
{
    private static readonly string[] ExpectedToolNames =
    [
        "fuse_context",
        "fuse_find",
        "fuse_index",
        "fuse_localize",
        "fuse_map",
        "fuse_reduce",
        "fuse_resolve",
        "fuse_review",
    ];

    [Fact]
    public async Task StdioServer_ListsTheEightV3Tools_AndFuseMapReturnsIndexedSymbols()
    {
        using var fixture = new McpFixtureProject();
        fixture.AddFile("Services/WidgetService.cs", """
            namespace App;
            public class WidgetService
            {
                public void Spin() { }
            }
            """);

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = DotNetExecutablePath(),
            Arguments = [FuseAssemblyPath(), "mcp", "serve"],
            Name = "fuse-mcp-integration-test",
        });

        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestCancellation);

        var tools = await client.ListToolsAsync(cancellationToken: TestCancellation);
        Assert.Equal(ExpectedToolNames, tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // fuse_map builds the index on first use (the fixture is a git repo, so the store stays inside it).
        var result = await client.CallToolAsync(
            "fuse_map",
            new Dictionary<string, object?> { ["path"] = fixture.ProjectPath, ["detail"] = "symbols" },
            cancellationToken: TestCancellation);

        var text = TextContent(result);
        Assert.Contains("workspace map", text);
        Assert.Contains("WidgetService", text);
    }

    private static CancellationToken TestCancellation => default;

    private static string DotNetExecutablePath() => "dotnet";

    private static string FuseAssemblyPath()
    {
        var location = typeof(FuseTools).Assembly.Location;
        return File.Exists(location)
            ? location
            : throw new InvalidOperationException($"Could not locate fuse.dll for MCP integration tests at '{location}'.");
    }

    private static string TextContent(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private sealed class McpFixtureProject : IDisposable
    {
        public string ProjectPath { get; } =
            Path.Combine(Path.GetTempPath(), "fuse-mcp-serve-tests", Guid.NewGuid().ToString("N"));

        public McpFixtureProject()
        {
            Directory.CreateDirectory(ProjectPath);
            // Initialize a git repo so FuseStorePaths resolves the index to {ProjectPath}/.fuse, isolating the
            // store from the machine-wide ~/.fuse used for non-git directories.
            RunGit("init");
        }

        private void RunGit(string arguments)
        {
            try
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = ProjectPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                process?.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Git not on PATH: the store falls back to the machine-wide location; the test still functions.
            }
        }

        public void AddFile(string relativePath, string content)
        {
            var fullPath = Path.Combine(ProjectPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(ProjectPath))
                Directory.Delete(ProjectPath, recursive: true);
        }
    }
}
