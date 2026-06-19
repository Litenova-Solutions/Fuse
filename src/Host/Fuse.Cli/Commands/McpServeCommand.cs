using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Fusion.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Commands;

/// <summary>
///     Starts the Fuse MCP server for AI agent integration over stdio.
/// </summary>
[CliCommand(
    Name = "serve",
    Description = "Start the Fuse MCP server for AI agent integration. Communicates via stdio using the Model Context Protocol.",
    Parent = typeof(FuseCliCommand))]
public sealed class McpServeCommand
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="McpServeCommand" /> class.
    /// </summary>
    public McpServeCommand()
    {
    }

    /// <summary>
    ///     Builds and runs the MCP host, serving the Fuse <see cref="FuseTools" /> and <see cref="FuseResources" />
    ///     over the stdio transport.
    /// </summary>
    /// <param name="context">The CLI invocation context supplying the cancellation token that shuts the host down.</param>
    /// <returns>A task that completes when the MCP host stops, either on cancellation or transport close.</returns>
    /// <remarks>
    ///     Starts a long-running host that owns stdin and stdout for JSON-RPC traffic. All logging is routed to
    ///     stderr (and <see cref="StderrConsoleUI" /> is registered) so that diagnostic output never corrupts the
    ///     protocol stream. The method blocks until <paramref name="context" />'s cancellation token is signalled.
    /// </remarks>
    public async Task RunAsync(CliContext context)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        builder.Services.AddSingleton<IConsoleUI, StderrConsoleUI>();
        builder.Services.AddFuse();

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "fuse",
                    Version = "2.0.0"
                };
                options.ServerInstructions =
                    "Fuse is a codebase context optimizer for AI-assisted workflows.\n\n" +
                    "TOOLS:\n" +
                    "- fuse_skeleton: Structural skeleton only (signatures, no bodies). Start here for architecture review.\n" +
                    "- fuse_focus: Dependency-aware scoping around a type, file, or path.\n" +
                    "- fuse_search: BM25 query-scoped fusion with dependency expansion.\n" +
                    "- fuse_changes: Git diff-scoped fusion for PR review.\n" +
                    "- fuse_dotnet: Full-control .NET fusion with all options combined.\n" +
                    "- fuse_generic: Generic fusion for any template (Python, Go, Rust, etc.).\n\n" +
                    "RECOMMENDED WORKFLOW:\n" +
                    "1. Call fuse_skeleton to get an architectural overview (low token cost).\n" +
                    "2. Identify the relevant area from the skeleton manifest.\n" +
                    "3. Call fuse_focus with focus=\"{TypeName}\" or fuse_search with query=\"{topic}\".\n" +
                    "4. For PR review, call fuse_changes with changedSince=\"{baseBranch}\".\n" +
                    "5. Use fuse_dotnet with all=true when you need maximum token reduction with full control.\n\n" +
                    "RESOURCES:\n" +
                    "- fuse://skeleton/{path}, fuse://focus/{path}/{seed}, fuse://search/{path}/{query}, fuse://changes/{path}/{since}\n" +
                    "- fuse://{template}/{path} for template-based fusion with default options.";
            })
            .WithStdioServerTransport()
            .WithTools<FuseTools>()
            .WithResources<FuseResources>();

        await builder.Build().RunAsync(context.CancellationToken);
    }
}
