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
        // Syntax-first cold start with a background semantic upgrade is opt-in: FUSE_BG_UPGRADE truthy
        // enables it. The default indexes synchronously on the first read (the proven-stable path), so no
        // detached background task outlives a request; making it the default is deferred with the resident-Roslyn-
        // workspace work that lets the host manage the background task's lifetime cleanly.
        FuseTools.BackgroundSemanticUpgradeEnabled = BackgroundUpgradeOptIn();
        // The resident host owns the background semantic-upgrade jobs' lifetime (N3): failures go to stderr (never
        // the JSON-RPC stdout), and shutdown drains them so none is orphaned. Replaces the old fire-and-forget path.
        FuseTools.UpgradeSupervisor = new Mcp.SemanticUpgradeSupervisor(Console.Error.WriteLine);

        // Tell the client (or auto-apply, when FUSE_AUTO_UPDATE is set) that a newer Fuse is available. Writes to
        // stderr so it never corrupts the JSON-RPC stream on stdout; cache-first, so it does not delay serving.
        FuseUpdatePrompt.Emit(Console.Error.WriteLine, allowAutoUpdate: true);

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
                    "THE LOOP: after an edit, fuse_check; before a signature change, fuse_impact; before done, fuse_review.\n\n" +
                    "Use fuse_review for PR/change work when a git base exists.\n" +
                    "Use fuse_find with kind=service|request|route|config when a task names wiring, or kind=task for an open-ended task.\n" +
                    "Use fuse_context only after fuse_find unless the user asks for one-shot context.\n" +
                    "Use fuse_find with kind=symbol|path|text for exact lookup.\n\n" +
                    "TOOLS:\n" +
                    "- fuse_workspace: Workspace status and lifecycle. action=status (index mode, verify grade, freshness), index (build/refresh), map (symbols/routes/counts), doctor (per-project load diagnosis), apply (write a proposed file edit - the one explicit tree-write path, D2; a dry run unless write=true, refuses paths outside the root). The cheap first call.\n" +
                    "- fuse_find: The find union. kind=symbol|path|text|all (exact lookup); service|request|route|config (wiring to impl/handler/action/options); signatures (a symbol's exact signature); neighbors (callers and implementers); task (rank candidate files, graded refuse-and-route). No bodies.\n" +
                    "- fuse_context: Emit source context (mixed tiers, manifest, provenance) for selected seeds.\n" +
                    "- fuse_review: Diff-first semantic impact and packed context for a change.\n" +
                    "- fuse_impact: Blast radius for a symbol (callers, implementers, referencers) before an edit; also package:{id,fromVersion,toVersion} for a NuGet upgrade break set.\n" +
                    "- fuse_check: Speculatively typecheck a proposed single-file edit (oracle-grade; abstains otherwise).\n" +
                    "- fuse_test: Run the covering tests for a symbol (build-grade, scoped by filter).\n" +
                    "- fuse_refactor: Compiler-executed, verify-gated refactors staged as a diff (rename, add/remove/reorder-parameter, add-cancellation-token, extract-interface, move-type, apply-codefix).\n" +
                    "- fuse_reduce: Compact a known set of files or raw content (the one utility outside the loop).";
            })
            .WithStdioServerTransport()
            .WithTools<FuseTools>()
            .WithResources<FuseResources>()
            // Register the playbook prompts (U3): selectable, anchored plans that teach the verified-edit loop.
            .WithPrompts<FusePrompts>();

        var app = builder.Build();

        // Resident workspace (S1) is opt-in for now (FUSE_RESIDENT truthy), default off so the shipped serve path
        // stays byte-identical until the latency gate promotes it. When on, warm the served root in the background
        // (never blocking startup) so fuse_check answers resident-grade from the held compilation, watch the tree
        // to keep it current, and project the changed cone into the store so store-backed reads reflect edits; a
        // bulk change above the storm threshold evicts to store-backed rather than serving stale. Wired after Build
        // so the SemanticIndexer (used for the store projection) is resolvable from the host services. Promotion to
        // default-on with the recorded latency and single-writer validation is the S1 gate.
        using var residentWatcher = ResidentWorkspaceHosting.OptIn()
            ? new DebouncedFileWatcher(Path.GetFullPath(Environment.CurrentDirectory), recursive: true, cancellationToken: context.CancellationToken)
            : null;
        using var resident = residentWatcher is null
            ? null
            : ResidentWorkspaceHosting.Enable(
                Environment.CurrentDirectory, residentWatcher,
                app.Services.GetRequiredService<Fuse.Semantics.SemanticIndexer>(), Console.Error.WriteLine, context.CancellationToken);

        try
        {
            await app.RunAsync(context.CancellationToken);
        }
        finally
        {
            // Cancel and drain in-flight background semantic upgrades so shutdown leaves no orphaned task.
            await FuseTools.UpgradeSupervisor.DisposeAsync();
        }
    }

    // The background semantic upgrade is opt-in: FUSE_BG_UPGRADE set to a truthy value (1/true/yes/on) enables
    // the syntax-first cold start; otherwise the first read indexes synchronously.
    private static bool BackgroundUpgradeOptIn()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_BG_UPGRADE");
        return value is not null
               && (value.Equals("1", StringComparison.Ordinal)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }
}
