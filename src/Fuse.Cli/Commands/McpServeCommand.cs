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
                    "Fuse is a codebase context optimizer for AI-assisted workflows.\n\n" +
                    "TOOLS:\n" +
                    "- fuse_dotnet: Optimized for .NET/C# projects. Supports skeleton mode (signatures only), " +
                    "semantic markers (structural annotations), focus scoping (dependency-aware subset), " +
                    "change scoping (git-diff-driven subset), and pattern summary (convention detection).\n" +
                    "- fuse_generic: Generic fusion for any template (Python, Go, Rust, etc.).\n\n" +
                    "RECOMMENDED WORKFLOW:\n" +
                    "1. Call fuse_dotnet with skeleton=true to get an architectural overview (low token cost).\n" +
                    "2. Identify the relevant area from the skeleton.\n" +
                    "3. Call fuse_dotnet with focus=\"{TypeName}\" to get full content for that area plus dependencies.\n" +
                    "4. For PR review, call fuse_dotnet with changedSince=\"{baseBranch}\" to scope to changed files.\n" +
                    "5. Use all=true for maximum token reduction when reviewing logic rather than exact syntax.";
            })
            .WithStdioServerTransport()
            .WithTools<FuseTools>()
            .WithResources<FuseResources>();

        await builder.Build().RunAsync(context.CancellationToken);
    }
}
