using System.Diagnostics;
using System.Reflection;
using DotMake.CommandLine;
using Fuse.Cli.Extensions;
using Fuse.Cli.Mcp;
using Fuse.Cli.Rpc;
using Fuse.Cli.Services;
using Fuse.Semantics;
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
        // Syntax-first cold start with a supervised background semantic upgrade is default-on. Set
        // FUSE_BG_UPGRADE=0 to make the first read index synchronously; `fuse index` is always synchronous.
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

        var mcpServer = builder.Services
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
                    "Fuse connects AI coding agents to the local .NET compiler and typed .NET wiring graph. " +
                    "It verifies proposed edits, computes change impact, runs covering tests, and stages " +
                    "compiler-verified refactors. Persistent indexed context supports these outcomes.\n\n" +
                    "THE LOOP: after an edit, fuse_check; before a signature change, fuse_impact; before done, fuse_review.\n\n" +
                    "VERIFICATION: fuse_check tries oracle-grade compiler state first. Without oracle state, it runs " +
                    "a scoped local dotnet build and returns build grade. It abstains only when neither path can run. " +
                    "The default server is store-backed and delegates resident-grade checks to a shared fuse host " +
                    "daemon per repository (set FUSE_DAEMON=0 to serve in-process). FUSE_RESIDENT=1 enables an " +
                    "opt-in in-process warm compilation path when the daemon is off; do not assume that speed on " +
                    "the default path.\n\n" +
                    "Use fuse_review for PR/change work when a git base exists.\n" +
                    "Use fuse_find with kind=service|request|route|config for typed .NET wiring, or kind=task for an open-ended task.\n" +
                    "Use fuse_context only after fuse_find unless the user asks for one-shot context.\n" +
                    "Use fuse_find with kind=symbol|path|text for exact lookup.\n\n" +
                    "TOOLS:\n" +
                    "- fuse_check: Typecheck a proposed single-file edit without writing it; oracle, then scoped local build, then abstain.\n" +
                    "- fuse_impact: Compute callers, implementers, referencers, and package-upgrade breaks before editing.\n" +
                    "- fuse_test: Run the covering tests for a symbol at build grade, scoped by filter.\n" +
                    "- fuse_review: Pack a diff's semantic impact and relevant context.\n" +
                    "- fuse_refactor: Stage compiler-executed, verify-gated refactors as diffs.\n" +
                    "- fuse_find: Find exact symbols and paths, typed wiring, signatures, neighbors, or task candidates. No bodies.\n" +
                    "- fuse_context: Emit scoped, reduced source with provenance for selected seeds.\n" +
                    "- fuse_workspace: Report status or manage the persistent index; apply is the only tree-write action and is dry-run unless write=true.\n" +
                    "- fuse_reduce: Compact known files or raw content.";
            });

        // F-017: opt-in System.Diagnostics.Metrics for tool duration, index mode, and reconcile-stamped events.
        if (FuseMetrics.Enabled)
        {
            // ModelContextProtocol 1.x moved per-request filters behind WithRequestFilters; the call-tool filter is
            // registered on the IMcpRequestFilterBuilder rather than directly on the server builder (0.8-preview API).
            mcpServer.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var sw = Stopwatch.StartNew();
                var toolName = context.Params?.Name ?? "unknown";
                try
                {
                    return await next(context, cancellationToken);
                }
                finally
                {
                    sw.Stop();
                    FuseMetrics.RecordToolDuration(toolName, sw.Elapsed);
                }
            }));
        }

        mcpServer
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
        // Shared daemon (G5, R13 default-on): serve ensures one shared `fuse host` daemon runs for this root
        // (spawning it on demand; the daemon's single-instance lock resolves any race) and delegates
        // resident-grade checks to it over the pipe, so one warm compilation serves every client. Set
        // FUSE_DAEMON=0 to hold an in-process workspace instead. Falls back to in-process when the daemon
        // cannot start.
        var daemonRoot = Path.GetFullPath(Environment.CurrentDirectory);
        using var daemonDelegation = IsDaemonEnabled()
            ? await TryAttachToDaemonAsync(daemonRoot, context.CancellationToken)
            : null;

        // Own resident workspace (S1) when not delegating to a daemon and FUSE_RESIDENT is opt-in.
        using var residentWatcher = daemonDelegation is null && ResidentWorkspaceHosting.OptIn()
            ? new DebouncedFileWatcher(daemonRoot, recursive: true, cancellationToken: context.CancellationToken)
            : null;
        using var warmSolutionWatcher = residentWatcher is null
            ? null
            : WarmSolutionCache.Shared.AttachWatcher(daemonRoot);
        if (residentWatcher is not null)
        {
            residentWatcher.BatchChanged += (batch, _) =>
            {
                if (batch.Any(change => WarmSolutionCache.IsTrackedSourceFile(daemonRoot, change.FullPath)))
                    WarmSolutionCache.Shared.InvalidateWatcherRoot(daemonRoot);
                return Task.CompletedTask;
            };
        }
        using var resident = residentWatcher is null
            ? null
            : ResidentWorkspaceHosting.Enable(
                daemonRoot, residentWatcher,
                app.Services.GetRequiredService<Fuse.Semantics.SemanticIndexer>(), Console.Error.WriteLine, context.CancellationToken);

        // R38: eager warm-on-start. When serving in-process (no daemon owns the index), kick off a background
        // syntax-first index for the served root the moment we start, so the first tool call hits a warm or a
        // bounded-building index (R27) rather than paying the full cold cost. Fire-and-forget and best-effort
        // (EagerIndex swallows build failures), so it never blocks or breaks startup. The daemon path warms in
        // fuse host instead. Opt out with FUSE_EAGER_INDEX=0.
        if (daemonDelegation is null)
        {
            _ = Mcp.EagerIndex.Start(app.Services.GetRequiredService<Fuse.Semantics.SemanticIndexer>(), daemonRoot);

            // R44: warm the MSBuild toolchain in the background when serving in-process (the delegating path warms
            // in fuse host instead), so the first fuse_refactor / doctor does not pay the locator-plus-first-load
            // warmup. Fire-and-forget; opt out with FUSE_MSBUILD_WARMUP=0.
            _ = Mcp.MsBuildToolchainWarmer.Start(daemonRoot, log: Console.Error.WriteLine, cancellationToken: context.CancellationToken);
        }

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

    // C3: syntax-first cold start with a supervised background semantic upgrade is default-ON in `mcp serve`, so a
    // first read returns in seconds (syntax tier) while the semantic/tier-1 graph builds behind it, supervised so
    // shutdown cancels and drains it (N3). Opt out with FUSE_BG_UPGRADE=0 (or false/no/off) to index synchronously
    // on the first read. The CLI `fuse index` is always synchronous regardless of this flag.
    private static bool BackgroundUpgradeOptIn()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_BG_UPGRADE");
        if (value is null)
            return true;
        return !(value.Equals("0", StringComparison.Ordinal)
                 || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    // The shared-daemon delegation (G5, R13 default-on; R19 index writes): serve ensures one shared `fuse host`
    // per repository serves every client unless FUSE_DAEMON=0 opts out.
    internal static bool IsDaemonEnabled()
    {
        var value = Environment.GetEnvironmentVariable("FUSE_DAEMON");
        if (value is null)
            return true;

        return !(value.Equals("0", StringComparison.Ordinal)
                 || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                 || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<RemoteDaemonDelegation?> TryAttachToDaemonAsync(string root, CancellationToken cancellationToken)
    {
        var supervisor = new DaemonSupervisor(
            ct => FuseHostClient.IsServingAsync(root, TimeSpan.FromMilliseconds(500), ct),
            () => DaemonProcessLauncher.Spawn(root, idleMinutes: 30));
        var outcome = await supervisor.EnsureRunningAsync(TimeSpan.FromSeconds(20), cancellationToken);
        if (outcome == DaemonSupervisor.Outcome.FailedToStart)
        {
            Console.Error.WriteLine($"Fuse serve: could not start a daemon for {root}; serving in-process.");
            return null;
        }

        var delegation = new RemoteDaemonDelegation();
        Console.Error.WriteLine(
            $"Fuse serve: resident workspace and index writes delegated to the shared daemon for {root} ({outcome}).");
        return delegation;
    }
}
