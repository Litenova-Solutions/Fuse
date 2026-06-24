using System.Diagnostics;
using System.Reflection;
using Fuse.Cli.Mcp;
using Fuse.Collection.Templates;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Scoping;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace Fuse.Cli.Rpc;

/// <summary>
///     The JSON-RPC surface the VS Code extension calls over the UI transport. One instance is shared by the
///     connection for a single repository root and delegates every measurement to the warm engine, so the UI and
///     the MCP agent read the same data. This first milestone exposes the lifecycle and health methods
///     (handshake, stats, shutdown); the engine-data projections (<c>fuse/index</c>, <c>fuse/graph</c>,
///     <c>fuse/scope</c>, <c>fuse/explain</c>, <c>fuse/diagnostics</c>) are layered on this same service.
/// </summary>
/// <remarks>
///     Method names use the <c>fuse/</c> namespace to match the wire protocol the extension's <c>protocol.ts</c>
///     mirrors. The service never throws across the wire for an expected condition; it returns a typed DTO so the
///     client can render a clear state rather than parse an error.
/// </remarks>
public sealed class FuseHostService
{
    /// <summary>
    ///     The wire protocol version. Bumped on any breaking change to a DTO or method shape so a stale extension
    ///     and a newer host detect the mismatch at handshake instead of failing later on a serialization error.
    /// </summary>
    public const int ProtocolVersion = 1;

    private readonly ILogger<FuseHostService> _logger;
    private readonly FusionOrchestrator _orchestrator;
    private readonly ProjectTemplateRegistry _templateRegistry;
    private readonly long _startTimestamp;
    private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseHostService" /> class.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator, shared with the MCP server and the CLI.</param>
    /// <param name="templateRegistry">The project-template registry that supplies the .NET fusion defaults.</param>
    /// <param name="logger">The logger for host-side diagnostics, routed away from the transport stream.</param>
    public FuseHostService(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        ILogger<FuseHostService> logger)
    {
        _orchestrator = orchestrator;
        _templateRegistry = templateRegistry;
        _logger = logger;
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    ///     A task that completes when a client calls <c>fuse/shutdown</c>, so the host can stop serving and exit.
    /// </summary>
    public Task ShutdownRequested => _shutdownRequested.Task;

    /// <summary>The host package version, read once from the assembly.</summary>
    public static string HostVersion =>
        typeof(FuseHostService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(FuseHostService).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>
    ///     Returns the host and protocol versions so the client can confirm it is talking to a compatible host.
    /// </summary>
    /// <returns>The host version and the wire protocol version.</returns>
    [JsonRpcMethod("fuse/handshake")]
    public FuseHostHandshake Handshake()
    {
        _logger.LogInformation("Handshake: host {HostVersion}, protocol {ProtocolVersion}.", HostVersion, ProtocolVersion);
        return new FuseHostHandshake(HostVersion, ProtocolVersion);
    }

    /// <summary>
    ///     Returns cheap process-level health for the status bar and index panel (host version, process id,
    ///     uptime, and working-set size shown as host RSS).
    /// </summary>
    /// <returns>The host process statistics.</returns>
    [JsonRpcMethod("fuse/stats")]
    public FuseHostStats Stats()
    {
        var uptimeMs = (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        using var process = Process.GetCurrentProcess();
        return new FuseHostStats(HostVersion, Environment.ProcessId, uptimeMs, process.WorkingSet64);
    }

    /// <summary>
    ///     Warms the engine for a repository root: collects the source tree and builds the analysis index and
    ///     dependency graph once through the shared orchestrator, so subsequent scoping calls pay only ranking
    ///     and emission. Returns the resulting index state and file count.
    /// </summary>
    /// <param name="root">The absolute repository root to index.</param>
    /// <returns>The warm-index state, the number of files considered, and the wall-clock build time.</returns>
    /// <remarks>
    ///     This runs the same in-memory .NET fusion path the MCP server uses (persistent index on), so the warm
    ///     state it builds is shared with the agent surface. A missing directory returns the
    ///     <c>NotIndexed</c> state rather than throwing across the wire.
    /// </remarks>
    [JsonRpcMethod("fuse/index")]
    public async Task<IndexResultDto> IndexAsync(string root)
    {
        var resolved = Path.GetFullPath(root);
        if (!Directory.Exists(resolved))
        {
            _logger.LogWarning("Index requested for missing directory {Root}.", resolved);
            return new IndexResultDto("NotIndexed", 0, 0);
        }

        var builder = FuseToolHelpers.CreateDotNetBuilder(_templateRegistry, resolved);
        var result = await _orchestrator.FuseAsync(builder.Build());
        _logger.LogInformation("Indexed {Root}: {Count} files in {Ms} ms.",
            resolved, result.TotalFileCount, (long)result.Duration.TotalMilliseconds);
        return new IndexResultDto("Warm", result.TotalFileCount, (long)result.Duration.TotalMilliseconds);
    }

    /// <summary>
    ///     Runs a scoped fusion through the shared orchestrator and returns the emitted file plan plus a path to
    ///     the written payload, so the extension can populate the scope-result panel and open the payload
    ///     read-only. The same orchestrator path the MCP agent uses, so the UI and the agent see identical scopes.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="mode">The scoping mode: <c>focus</c>, <c>changes</c>, or anything else for <c>search</c>.</param>
    /// <param name="seed">The focus seed (type or file) when <paramref name="mode" /> is <c>focus</c>.</param>
    /// <param name="query">The search query when <paramref name="mode" /> is <c>search</c>.</param>
    /// <param name="since">The git base when <paramref name="mode" /> is <c>changes</c>.</param>
    /// <param name="maxTokens">The token budget for the emitted payload, or <c>0</c> for unbounded.</param>
    /// <returns>The emitted files with token costs, the total tokens, and the payload file path.</returns>
    [JsonRpcMethod("fuse/scope")]
    public async Task<ScopeResultDto> ScopeAsync(
        string root, string mode, string? seed, string? query, string? since, int maxTokens)
    {
        var resolved = Path.GetFullPath(root);
        var normalizedMode = (mode ?? "search").Trim().ToLowerInvariant();
        if (!Directory.Exists(resolved))
            return new ScopeResultDto(normalizedMode, [], 0, null);

        var builder = FuseToolHelpers.CreateDotNetBuilder(_templateRegistry, resolved);
        builder.WithEmissionOptions(new EmissionOptions
        {
            MaxTokens = maxTokens > 0 ? maxTokens : null,
            ShowTokenCount = false,
            IncludeManifest = true,
        });

        switch (normalizedMode)
        {
            case "focus" when !string.IsNullOrWhiteSpace(seed):
                builder.WithFocusOptions(new FocusOptions(seed, Depth: 2));
                break;
            case "changes" when !string.IsNullOrWhiteSpace(since):
                builder.WithChangeOptions(new ChangeOptions(since));
                break;
            default:
                normalizedMode = "search";
                builder.WithQueryOptions(new QueryOptions(query ?? string.Empty, TopFiles: 10, Depth: 2));
                break;
        }

        var result = await _orchestrator.FuseAsync(builder.Build());

        string? payloadPath = null;
        if (!string.IsNullOrEmpty(result.InMemoryContent))
        {
            // Write the payload to a per-root temp file the extension opens read-only. Reused across scopes so
            // the host does not accumulate temp files; the extension reads it immediately after the response.
            var dir = Path.Combine(Path.GetTempPath(), "fuse-host-payloads");
            Directory.CreateDirectory(dir);
            payloadPath = Path.Combine(dir, HostEndpoint.PipeName(resolved) + "-" + normalizedMode + ".fuse.xml");
            await File.WriteAllTextAsync(payloadPath, result.InMemoryContent);
        }

        var files = result.EmittedFileTokens
            .Select(f => new ScopeFileDto(f.Path, (int)f.Count))
            .ToList();

        _logger.LogInformation("Scope {Mode} on {Root}: {Files} files, {Tokens} tokens.",
            normalizedMode, resolved, files.Count, result.TotalTokens);
        return new ScopeResultDto(normalizedMode, files, result.TotalTokens, payloadPath);
    }

    /// <summary>
    ///     Signals the host to flush and exit. The transport completes the in-flight response before the host
    ///     stops serving.
    /// </summary>
    [JsonRpcMethod("fuse/shutdown")]
    public void Shutdown()
    {
        _logger.LogInformation("Shutdown requested by client.");
        _shutdownRequested.TrySetResult();
    }
}
