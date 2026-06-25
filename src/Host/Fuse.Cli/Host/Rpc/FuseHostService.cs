using System.Diagnostics;
using System.Reflection;
using Fuse.Cli.Mcp;
using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Templates;
using Fuse.Emission.Models;
using Fuse.Fusion;
using Fuse.Fusion.Scoping;
using Fuse.Plugins.Abstractions;
using Fuse.Plugins.Abstractions.Dependencies;
using Fuse.Plugins.Languages.CSharp.Reducers;
using Fuse.Reduction.Security;
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
    private readonly FileCollectionPipeline _collectionPipeline;
    private readonly DependencyGraphBuilder _graphBuilder;
    private readonly Func<ISourceContentProvider> _contentProviderFactory;
    private readonly CapabilityRegistry<IDependencyExtractor> _dependencyExtractors;
    private readonly CapabilityRegistry<ITypeNameLocator> _typeNameLocators;
    private readonly ISecretRedactor _redactor;
    private readonly long _startTimestamp;
    private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Initializes a new instance of the <see cref="FuseHostService" /> class.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator, shared with the MCP server and the CLI.</param>
    /// <param name="templateRegistry">The project-template registry that supplies the .NET fusion defaults.</param>
    /// <param name="collectionPipeline">The file collection pipeline used to enumerate the source tree for the graph.</param>
    /// <param name="graphBuilder">The dependency-graph builder, shared with the engine.</param>
    /// <param name="contentProviderFactory">Factory for a per-call source content provider.</param>
    /// <param name="dependencyExtractors">Per-extension referenced-type extractors.</param>
    /// <param name="typeNameLocators">Per-extension declared-type locators.</param>
    /// <param name="redactor">The secret redactor, used read-only to locate secret spans for diagnostics.</param>
    /// <param name="logger">The logger for host-side diagnostics, routed away from the transport stream.</param>
    public FuseHostService(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        FileCollectionPipeline collectionPipeline,
        DependencyGraphBuilder graphBuilder,
        Func<ISourceContentProvider> contentProviderFactory,
        CapabilityRegistry<IDependencyExtractor> dependencyExtractors,
        CapabilityRegistry<ITypeNameLocator> typeNameLocators,
        ISecretRedactor redactor,
        ILogger<FuseHostService> logger)
    {
        _orchestrator = orchestrator;
        _templateRegistry = templateRegistry;
        _collectionPipeline = collectionPipeline;
        _graphBuilder = graphBuilder;
        _contentProviderFactory = contentProviderFactory;
        _dependencyExtractors = dependencyExtractors;
        _typeNameLocators = typeNameLocators;
        _redactor = redactor;
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
    ///     Projects the dependency graph for a repository root: nodes are files (or directories at the coarser
    ///     level of detail) with PageRank centrality and an estimated token cost, and edges are file-to-file
    ///     references. The webview sizes nodes by centrality, colors them by token cost, and styles edges by kind.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="detail">
    ///     <c>Files</c> for a node per file, or <c>Directories</c> for directory supernodes (file nodes folded
    ///     into their directory and edges aggregated), so a large repository does not ship its whole file graph.
    /// </param>
    /// <returns>The graph nodes and edges at the requested level of detail.</returns>
    /// <remarks>
    ///     The token cost is a cheap size-based estimate (bytes divided by four), sufficient for relative node
    ///     coloring; it is not the exact o200k count the emission path reports. Uses the same collection pipeline
    ///     and dependency-graph builder as the engine, so the projection matches what scoping traverses.
    /// </remarks>
    [JsonRpcMethod("fuse/graph")]
    public async Task<GraphDto> GraphAsync(
        string root, string detail, string? scopeMode = null, string? seed = null, string? query = null,
        string? since = null, string? directory = null)
    {
        var resolved = Path.GetFullPath(root);
        // A directory filter expands one supernode: it forces file-level nodes restricted to that subtree, so a
        // large repository ships its directory graph first and a directory's files only when the user expands it.
        var expandDirectory = !string.IsNullOrWhiteSpace(directory);
        var directories = !expandDirectory && string.Equals(detail, "Directories", StringComparison.OrdinalIgnoreCase);
        if (!Directory.Exists(resolved))
            return new GraphDto([], [], directories ? "Directories" : "Files");

        var request = FuseToolHelpers.CreateDotNetBuilder(_templateRegistry, resolved).Build();
        var collection = await _collectionPipeline.CollectAsync(request.Collection);
        var graph = await _graphBuilder.BuildAsync(
            collection.Files, _contentProviderFactory(), _dependencyExtractors, _typeNameLocators);
        var centrality = GraphCentrality.Compute(graph);

        // Estimate per-file token cost from byte length (bytes / 4), a cheap relative signal for node coloring.
        var tokenByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in collection.Files)
            tokenByPath[file.NormalizedRelativePath] = (int)(file.FileInfo.Length / 4);

        // Optional scope overlay: when a scope is supplied, run it and tag each node with the role the context
        // plan gave that file (Seed, Dependency, Changed), so the webview can recolor by role to show exactly
        // what a fusion would include. The roles apply at file granularity (they do not aggregate to directories).
        var roleByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(scopeMode))
        {
            var scopeBuilder = FuseToolHelpers.CreateDotNetBuilder(_templateRegistry, resolved);
            ApplyScopeMode(scopeBuilder, scopeMode, seed, query, since);
            var scoped = await _orchestrator.FuseAsync(scopeBuilder.Build());
            foreach (var planned in scoped.Plan)
                roleByPath[planned.Path] = planned.Role;
        }

        var fileNodes = new List<GraphNodeDto>(graph.DeclaredTypes.Count);
        foreach (var (path, types) in graph.DeclaredTypes)
        {
            fileNodes.Add(new GraphNodeDto(
                path,
                types,
                centrality.TryGetValue(path, out var c) ? Math.Round(c, 4) : 0.0,
                tokenByPath.GetValueOrDefault(path),
                roleByPath.GetValueOrDefault(path)));
        }

        var fileEdges = new List<GraphEdgeDto>();
        foreach (var (from, targets) in graph.FileReferences)
            foreach (var to in targets)
                fileEdges.Add(new GraphEdgeDto(from, to, 1.0, "reference"));

        if (expandDirectory)
        {
            // Restrict to the requested directory subtree (the supernode the user expanded) and the edges among
            // its files, so an expand ships one directory's file graph rather than the whole repository.
            var prefix = directory!.Replace('\\', '/').TrimEnd('/') + "/";
            bool Under(string p) => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            var subNodes = fileNodes.Where(n => Under(n.Path)).ToList();
            var subPaths = new HashSet<string>(subNodes.Select(n => n.Path), StringComparer.OrdinalIgnoreCase);
            var subEdges = fileEdges.Where(e => subPaths.Contains(e.From) && subPaths.Contains(e.To)).ToList();
            return new GraphDto(subNodes, subEdges, "Files");
        }

        if (!directories)
            return new GraphDto(fileNodes, fileEdges, "Files");

        return AggregateToDirectories(fileNodes, fileEdges);
    }

    // Applies the requested scoping mode to a request builder and returns the normalized mode. Shared by scope,
    // explain, and the graph role overlay so the focus/changes/search routing is defined once. Falls back to
    // search when a focus seed or git base is absent.
    private static string ApplyScopeMode(FusionRequestBuilder builder, string? mode, string? seed, string? query, string? since)
    {
        var normalized = (mode ?? "search").Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "focus" when !string.IsNullOrWhiteSpace(seed):
                builder.WithFocusOptions(new FocusOptions(seed, Depth: 2));
                break;
            case "changes" when !string.IsNullOrWhiteSpace(since):
                builder.WithChangeOptions(new ChangeOptions(since));
                break;
            default:
                normalized = "search";
                builder.WithQueryOptions(new QueryOptions(query ?? string.Empty, TopFiles: 10, Depth: 2));
                break;
        }

        return normalized;
    }

    // Folds the file graph into directory supernodes: a node per directory (token cost summed, centrality summed
    // for relative sizing) and one edge per distinct cross-directory reference, so a large repository ships a
    // small graph that the webview expands on demand. Mirrors the engine's directory-level table of contents.
    private static GraphDto AggregateToDirectories(IReadOnlyList<GraphNodeDto> fileNodes, IReadOnlyList<GraphEdgeDto> fileEdges)
    {
        static string DirectoryOf(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash <= 0 ? "." : path[..slash];
        }

        var byDir = new Dictionary<string, (double Centrality, int Tokens, int Files)>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in fileNodes)
        {
            var dir = DirectoryOf(node.Path);
            var acc = byDir.GetValueOrDefault(dir);
            byDir[dir] = (acc.Centrality + node.Centrality, acc.Tokens + node.TokenCost, acc.Files + 1);
        }

        var dirNodes = byDir
            .Select(kv => new GraphNodeDto(kv.Key, [$"{kv.Value.Files} files"], Math.Round(kv.Value.Centrality, 4), kv.Value.Tokens, null))
            .ToList();

        var dirEdges = fileEdges
            .Select(e => (From: DirectoryOf(e.From), To: DirectoryOf(e.To)))
            .Where(e => !string.Equals(e.From, e.To, StringComparison.OrdinalIgnoreCase))
            .GroupBy(e => (e.From, e.To))
            .Select(g => new GraphEdgeDto(g.Key.From, g.Key.To, g.Count(), "reference"))
            .ToList();

        return new GraphDto(dirNodes, dirEdges, "Directories");
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
        if (!Directory.Exists(resolved))
            return new ScopeResultDto((mode ?? "search").Trim().ToLowerInvariant(), [], 0, null);

        var builder = FuseToolHelpers.CreateDotNetBuilder(_templateRegistry, resolved);
        builder.WithEmissionOptions(new EmissionOptions
        {
            MaxTokens = maxTokens > 0 ? maxTokens : null,
            ShowTokenCount = false,
            IncludeManifest = true,
        });

        var normalizedMode = ApplyScopeMode(builder, mode, seed, query, since);
        var result = await _orchestrator.FuseAsync(builder.Build());

        string? payloadPath = null;
        if (!string.IsNullOrEmpty(result.InMemoryContent))
        {
            // Write the payload to a temp file the extension opens read-only. The name is unique per call (a
            // GUID) so concurrent scopes on the same root and mode do not contend on one file; the extension
            // reads it immediately after the response. The OS temp directory reclaims these.
            var dir = Path.Combine(Path.GetTempPath(), "fuse-host-payloads");
            Directory.CreateDirectory(dir);
            payloadPath = Path.Combine(
                dir, $"{HostEndpoint.PipeName(resolved)}-{normalizedMode}-{Guid.NewGuid():N}.fuse.xml");
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
    ///     Scans the repository for context diagnostics. Currently returns secret findings with precise editor
    ///     ranges, computed read-only with the same redactor the reduction path uses, so the editor underlines
    ///     exactly the literal that emitted output would redact. Hotspot and graph-gap diagnostics are layered on
    ///     this method in later increments.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <returns>The detected secrets with their precise line and character ranges.</returns>
    [JsonRpcMethod("fuse/diagnostics")]
    public async Task<DiagnosticsDto> DiagnosticsAsync(string root)
    {
        var resolved = Path.GetFullPath(root);
        if (!Directory.Exists(resolved))
            return new DiagnosticsDto([], [], [], []);

        var request = FuseToolHelpers.CreateDotNetBuilder(_templateRegistry, resolved).Build();
        var collection = await _collectionPipeline.CollectAsync(request.Collection);
        var contentProvider = _contentProviderFactory();

        var secrets = new List<SecretDiagnosticDto>();
        var generated = new List<string>();
        foreach (var file in collection.Files)
        {
            var content = await contentProvider.GetContentAsync(file);

            // Flag generated C# (EF Core migrations and model snapshots, rarely worth reading) for an editor hint.
            if (file.IsCSharp && GeneratedCodeCollapser.IsGenerated(content))
                generated.Add(file.NormalizedRelativePath);

            var spans = _redactor.FindSecretSpans(content);
            if (spans.Count == 0)
                continue;

            // Convert each character span to a zero-based line and column range once per file, walking the
            // content's newline offsets so a multi-line file maps spans without rescanning from the start.
            var lineStarts = ComputeLineStarts(content);
            foreach (var span in spans)
            {
                var (startLine, startCol) = OffsetToLineColumn(lineStarts, span.Start);
                var (endLine, endCol) = OffsetToLineColumn(lineStarts, span.Start + span.Length);
                secrets.Add(new SecretDiagnosticDto(
                    file.NormalizedRelativePath, span.Kind, startLine, startCol, endLine, endCol));
            }
        }

        // Token hotspots and graph gaps from the dependency graph: hotspots are the most token-expensive files
        // (the budget pressure), gaps are files with no inbound or outbound type reference (often dead or
        // reflection-only code the syntax graph cannot see).
        var graph = await _graphBuilder.BuildAsync(collection.Files, contentProvider, _dependencyExtractors, _typeNameLocators);
        var hotspots = collection.Files
            .Select(f => new HotspotDiagnosticDto(f.NormalizedRelativePath, (int)(f.FileInfo.Length / 4)))
            .OrderByDescending(h => h.TokenCost)
            .Take(20)
            .ToList();

        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, targets) in graph.FileReferences)
        {
            if (targets.Count > 0)
                connected.Add(from);
            foreach (var to in targets)
                connected.Add(to);
        }
        var graphGaps = graph.DeclaredTypes.Keys
            .Where(p => !connected.Contains(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _logger.LogInformation("Diagnostics on {Root}: {Secrets} secrets, {Hotspots} hotspots, {Gaps} gaps, {Generated} generated.",
            resolved, secrets.Count, hotspots.Count, graphGaps.Count, generated.Count);
        return new DiagnosticsDto(secrets, hotspots, graphGaps, generated);
    }

    // The character offset at which each line starts, so an offset maps to a line by binary search.
    private static int[] ComputeLineStarts(string content)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < content.Length; i++)
            if (content[i] == '\n')
                starts.Add(i + 1);
        return [.. starts];
    }

    // Maps a character offset to a zero-based (line, column) using the precomputed line-start table.
    private static (int Line, int Column) OffsetToLineColumn(int[] lineStarts, int offset)
    {
        var line = Array.BinarySearch(lineStarts, offset);
        if (line < 0)
            line = ~line - 1; // the insertion point minus one is the line whose start is at or before the offset
        line = Math.Clamp(line, 0, lineStarts.Length - 1);
        return (line, offset - lineStarts[line]);
    }

    /// <summary>
    ///     Explains what a scoped fusion would include without writing a payload: returns the context plan
    ///     (each planned file's role, reduction tier, and relevance score) the same orchestrator builds for a
    ///     real scope, so the extension's scope-result and explainer panels can show why a file is in and at what
    ///     fidelity before fetching anything.
    /// </summary>
    /// <param name="root">The absolute repository root.</param>
    /// <param name="mode">The scoping mode: <c>focus</c>, <c>changes</c>, or anything else for <c>search</c>.</param>
    /// <param name="seed">The focus seed when <paramref name="mode" /> is <c>focus</c>.</param>
    /// <param name="query">The search query when <paramref name="mode" /> is <c>search</c>.</param>
    /// <param name="since">The git base when <paramref name="mode" /> is <c>changes</c>.</param>
    /// <returns>The scoping mode and the planned files with their roles, tiers, and scores.</returns>
    [JsonRpcMethod("fuse/explain")]
    public async Task<ExplainResultDto> ExplainAsync(string root, string mode, string? seed, string? query, string? since)
    {
        var resolved = Path.GetFullPath(root);
        if (!Directory.Exists(resolved))
            return new ExplainResultDto((mode ?? "search").Trim().ToLowerInvariant(), []);

        var builder = FuseToolHelpers.CreateDotNetBuilder(_templateRegistry, resolved);
        var normalizedMode = ApplyScopeMode(builder, mode, seed, query, since);
        var result = await _orchestrator.FuseAsync(builder.Build());
        var files = result.Plan
            .Select(p => new ExplainFileDto(p.Path, p.Role, p.Tier, p.Score))
            .ToList();

        _logger.LogInformation("Explain {Mode} on {Root}: {Files} planned files.", normalizedMode, resolved, files.Count);
        return new ExplainResultDto(normalizedMode, files);
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
