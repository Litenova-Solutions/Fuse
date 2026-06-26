using System.Reflection;
using DotMake.CommandLine;
using Fuse.Cli.Extensions;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
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
    Parent = typeof(McpCommand))]
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
                    Version = typeof(McpServeCommand).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                        ?? typeof(McpServeCommand).Assembly.GetName().Version?.ToString(3)
                        ?? "0.0.0"
                };
                options.ServerInstructions =
                    "Fuse is a .NET semantic context engine for AI agents. It indexes a workspace with Roslyn and " +
                    "serves precise, provenance-backed context from a typed semantic graph.\n\n" +
                    "Use fuse_review for PR/change work when a git base exists.\n" +
                    "Use fuse_resolve when a task names a route, interface, service, request, handler, or config section.\n" +
                    "Use fuse_localize for open-ended tasks.\n" +
                    "Use fuse_context only after localize/resolve unless the user asks for one-shot context.\n" +
                    "Use fuse_find for exact text/path/symbol lookup.\n\n" +
                    "TOOLS:\n" +
                    "- fuse_index: Build or refresh the persistent semantic index. The read tools build it on first use.\n" +
                    "- fuse_map: Workspace map (symbols, routes, counts). The cheap first call.\n" +
                    "- fuse_localize: Rank candidate files/symbols for a task. No bodies.\n" +
                    "- fuse_resolve: Resolve wiring (service->impl, request->handler, route->action, config->options, symbol). No bodies.\n" +
                    "- fuse_context: Emit source context (mixed tiers, manifest, provenance) for selected seeds.\n" +
                    "- fuse_review: Diff-first semantic impact and packed context for a change.\n" +
                    "- fuse_find: Exact symbol/path/text lookup.\n" +
                    "- fuse_reduce: Compact a known set of files or raw content.";
            })
            .WithStdioServerTransport()
            .WithTools<FuseTools>()
            .WithResources<FuseResources>();

        await builder.Build().RunAsync(context.CancellationToken);
    }
}
