using DotMake.CommandLine;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Fusion.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Commands;

[CliCommand(
    Name = "serve",
    Description = "Start the Fuse MCP server for AI agent integration. Communicates via stdio using the Model Context Protocol.",
    Parent = typeof(FuseCliCommand))]
public sealed class McpServeCommand
{
    public McpServeCommand()
    {
    }

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
                    "Fuse is a codebase context optimizer. Use fuse_dotnet for .NET projects or fuse_generic " +
                    "for other templates. You can also read fuse:// resources for optimized views of codebases.";
            })
            .WithStdioServerTransport()
            .WithTools<FuseTools>()
            .WithResources<FuseResources>();

        await builder.Build().RunAsync(context.CancellationToken);
    }
}
