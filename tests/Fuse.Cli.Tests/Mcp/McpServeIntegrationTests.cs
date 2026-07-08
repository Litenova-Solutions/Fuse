using Fuse.Cli.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Fuse.Cli.Tests.Mcp;

/// <summary>
///     End-to-end tests that spawn <c>fuse mcp serve</c> as a stdio subprocess and drive the MCP client protocol.
/// </summary>
public sealed class McpServeIntegrationTests
{
    private static readonly string[] ExpectedV3ToolNames =
    [
        "fuse_changeset",
        "fuse_check",
        "fuse_context",
        "fuse_find",
        "fuse_impact",
        "fuse_index",
        "fuse_localize",
        "fuse_map",
        "fuse_neighbors",
        "fuse_reduce",
        "fuse_refactor",
        "fuse_resolve",
        "fuse_review",
        "fuse_signatures",
    ];

    // The retired V2 names are re-registered as deprecation shims (FuseDeprecatedTools) so a client that cached
    // the old surface across an upgrade gets an actionable message instead of an Unknown tool error.
    private static readonly string[] ExpectedDeprecatedToolNames =
    [
        "fuse_ask",
        "fuse_changes",
        "fuse_dotnet",
        "fuse_focus",
        "fuse_generic",
        "fuse_search",
        "fuse_skeleton",
        "fuse_toc",
    ];

    [Fact]
    public async Task StdioServer_ListsV3ToolsAndDeprecationShims_AndFuseMapReturnsIndexedSymbols()
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
        var expected = ExpectedV3ToolNames.Concat(ExpectedDeprecatedToolNames).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // fuse_map builds the index on first use (the fixture is a git repo, so the store stays inside it).
        var result = await client.CallToolAsync(
            "fuse_map",
            new Dictionary<string, object?> { ["path"] = fixture.ProjectPath, ["detail"] = "symbols" },
            cancellationToken: TestCancellation);

        var text = TextContent(result);
        Assert.Contains("workspace map", text);
        Assert.Contains("WidgetService", text);

        // fuse_impact carries the T2 public-surface line: a public type is flagged as external-facing so the agent
        // knows a change to it is contract-relevant before editing.
        var impact = await client.CallToolAsync(
            "fuse_impact",
            new Dictionary<string, object?> { ["path"] = fixture.ProjectPath, ["symbol"] = "WidgetService" },
            cancellationToken: TestCancellation);

        var impactText = TextContent(impact);
        Assert.Contains("public API surface:", impactText);
        Assert.Contains("WidgetService is on the public/protected surface", impactText);
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
            // The serve subprocess may run a background semantic upgrade that holds the index db open briefly; on
            // shutdown the OS releases that handle a moment after the process is signalled, so retry the delete
            // through the transient IOException rather than failing teardown on the race.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    if (Directory.Exists(ProjectPath))
                        Directory.Delete(ProjectPath, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }
}
