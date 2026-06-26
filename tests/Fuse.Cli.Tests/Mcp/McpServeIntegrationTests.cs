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
        "fuse_ask",
        "fuse_changes",
        "fuse_dotnet",
        "fuse_explain",
        "fuse_find",
        "fuse_focus",
        "fuse_generic",
        "fuse_reduce",
        "fuse_search",
        "fuse_skeleton",
        "fuse_toc",
    ];

    [Fact]
    public async Task StdioServer_ListsElevenTools_AndFuseTocReturnsFixtureOutline()
    {
        using var fixture = new McpFixtureProject();
        fixture.AddFile("Services/WidgetService.cs", """
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

        var result = await client.CallToolAsync(
            "fuse_toc",
            new Dictionary<string, object?> { ["path"] = fixture.ProjectPath },
            cancellationToken: TestCancellation);

        var text = TextContent(result);
        Assert.Contains("fuse:table-of-contents", text);
        Assert.Contains("WidgetService.cs", text);
        Assert.Contains("class WidgetService", text);
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

        public McpFixtureProject() => Directory.CreateDirectory(ProjectPath);

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
