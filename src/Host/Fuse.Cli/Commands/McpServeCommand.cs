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
                    "Fuse is a codebase context optimizer for AI-assisted workflows.\n\n" +
                    "Prefer these tools over raw grep or reading files one by one when surveying or scoping a codebase; " +
                    "reach for grep only for exact-string or symbol lookups.\n\n" +
                    "TOOLS:\n" +
                    "- fuse_toc: Table of contents (directory tree, symbol outline, per-file token costs). The cheapest first call.\n" +
                    "- fuse_skeleton: Structural skeleton only (signatures, no bodies). Use for architecture review.\n" +
                    "- fuse_focus: Dependency-aware scoping around a type, file, or path.\n" +
                    "- fuse_search: BM25 query-scoped fusion with dependency expansion.\n" +
                    "- fuse_changes: Git diff-scoped fusion for PR review.\n" +
                    "- fuse_ask: Give a task and token budget; Fuse picks skeleton, focus, or search and packs to budget.\n" +
                    "- fuse_dotnet: Full-control .NET fusion with all options combined.\n" +
                    "- fuse_generic: Generic fusion for any template (Python, Go, Rust, etc.).\n" +
                    "- fuse_reduce: Compact a specific set of files (or raw content) you already identified, without collecting a whole directory.\n" +
                    "- fuse_explain: Preview which files a scoped fusion would include and exclude, with a token estimate, before fetching.\n\n" +
                    "CHOOSING A MODE (most accurate first):\n" +
                    "- Branch, PR, or fix work with a git base: prefer fuse_changes with changedSince=\"{base}\". " +
                    "It has by far the highest recall of the files a task touches, because it starts from the diff.\n" +
                    "- Exploring or editing a specific type: fuse_focus with focus=\"{TypeName}\".\n" +
                    "- Finding where a concept or feature lives: fuse_search with query=\"{topic}\".\n" +
                    "- Broad survey first: fuse_toc, then a scoped fetch.\n" +
                    "- Unsure: fuse_ask with the task and tokenBudget, and Fuse routes for you.\n\n" +
                    "RECOMMENDED WORKFLOW:\n" +
                    "1. Call fuse_toc (or fuse_skeleton) to survey the codebase at low token cost.\n" +
                    "2. Identify the relevant area from the tree and per-file token costs.\n" +
                    "3. Call fuse_focus with focus=\"{TypeName}\" or fuse_search with query=\"{topic}\".\n" +
                    "4. For branch, PR, or fix work, call fuse_changes with changedSince=\"{baseBranch}\" (highest recall).\n" +
                    "5. Or call fuse_ask with a task and tokenBudget to let Fuse choose and pack the context.\n\n" +
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
