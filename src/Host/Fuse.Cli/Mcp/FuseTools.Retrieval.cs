using System.ComponentModel;
using System.Text;
using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Reduction;
using Fuse.Retrieval;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     The retrieval MCP tools (localize, resolve, context, review) over the persistent semantic index.
/// </summary>
public sealed partial class FuseTools
{
    /// <summary>
    ///     Localizes a task to ranked candidate files and symbols (no source bodies).
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="changeSource">The change source for resolving a git base ref.</param>
    /// <param name="embedder">An optional text embedder; when present, a dense retrieval channel is added.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="task">The free-text task or query.</param>
    /// <param name="route">A route to resolve.</param>
    /// <param name="symbol">A symbol to focus on.</param>
    /// <param name="service">A service to resolve.</param>
    /// <param name="request">A request or command to resolve.</param>
    /// <param name="config">A config section to resolve.</param>
    /// <param name="changedSince">A git base ref whose changed files seed candidates.</param>
    /// <param name="maxCandidates">The maximum candidates to return.</param>
    /// <param name="strict">When true, an insufficient request is refused and only a navigation map is returned; off by default (best-effort).</param>
    /// <param name="expand">When true, the selected candidates are enriched with their typed-graph neighbors for discovery; off by default.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>Ranked candidates with reasons and token costs, or a navigation map when the request is not confident.</returns>
    [McpServerTool(Name = "fuse_localize", ReadOnly = true)]
    [Description("Localize a task to ranked candidate files and symbols (no bodies). The cheap first step of an iterative workflow; follow with fuse_context to read selected seeds.")]
    public static async Task<string> FuseLocalizeAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        Fuse.Plugins.Abstractions.Scoping.ITextEmbedder? embedder = null,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The task or query to localize.")] string? task = null,
        [Description("A route to resolve, for example \"POST /api/orders/{id}\".")] string? route = null,
        [Description("A symbol to focus on.")] string? symbol = null,
        [Description("A service to resolve to its implementation.")] string? service = null,
        [Description("A request/command to resolve to its handler.")] string? request = null,
        [Description("A config section to resolve to its options type.")] string? config = null,
        [Description("A git base ref whose changed files seed the candidates.")] string? changedSince = null,
        [Description("Maximum candidates to return.")] int maxCandidates = 50,
        [Description("Strict signal-sufficiency: when an insufficient request has no clear anchor, refuse and return only a navigation map instead of a low-confidence guess. Off by default (best-effort).")] bool strict = false,
        [Description("Expand the selected candidates with their typed-graph neighbors (implementers, callers, config) for discovery. Off by default; widens recall but pressures precision.")] bool expand = false,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store, changeSource, embedder);
        var requestModel = new LocalizationRequest(
            root, Query: task, ChangedSince: changedSince, Route: route, Focus: symbol, Service: service,
            Request: request, ConfigSection: config, MaxCandidates: maxCandidates, Strict: strict, ExpandGraph: expand);
        var result = await engine.LocalizeAsync(requestModel, cancellationToken);
        return LocalizationFormatter.Format(result);
    }

    /// <summary>
    ///     Iterative exploration primitives: the graph neighborhood of a file, the callers and implementers of a
    ///     symbol, or the structurally central files of an area. Ranked, bounded, and body-free, for chaining.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="file">A file whose graph neighborhood to return.</param>
    /// <param name="symbol">A symbol whose callers and implementers to return.</param>
    /// <param name="centralIn">An area (folder prefix, or empty for the whole workspace) whose central files to return.</param>
    /// <param name="limit">The maximum results to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The ranked exploration items with provenance and no bodies.</returns>
    [McpServerTool(Name = "fuse_neighbors", ReadOnly = true)]
    [Description("Iterative exploration primitives (no bodies): the graph neighborhood of a file (callers, implementers, consumers, config, plus same-folder cohesion), the callers and implementers of a symbol, or the structurally central files of an area. Chain these to turn a weak first guess into a strong few-call funnel.")]
    public static async Task<string> FuseNeighborsAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("A file whose graph neighborhood to return.")] string? file = null,
        [Description("A symbol whose callers and implementers to return.")] string? symbol = null,
        [Description("An area (folder prefix, or empty for the whole workspace) whose central files to return.")] string? centralIn = null,
        [Description("Maximum results to return.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var explorer = new GraphNeighborhoodExplorer(store);

        string mode;
        IReadOnlyList<ExploredItem> items;
        if (!string.IsNullOrWhiteSpace(file))
        {
            mode = $"neighborhood of {file}";
            items = await explorer.NeighborhoodAsync(file, limit, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(symbol))
        {
            mode = $"callers and implementers of {symbol}";
            items = await explorer.CallersAndImplementersAsync(symbol, limit, cancellationToken);
        }
        else if (centralIn is not null)
        {
            mode = centralIn.Length == 0 ? "central files (workspace)" : $"central files in {centralIn}";
            items = await explorer.CentralFilesAsync(centralIn, limit, cancellationToken);
        }
        else
        {
            return "Error: specify one of file, symbol, or centralIn.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"neighbors ({mode}): {items.Count}");
        foreach (var item in items)
        {
            var symbolPart = item.Symbol is null ? string.Empty : $"  {item.Symbol}";
            builder.AppendLine($"  {item.Path}{symbolPart}  [{item.Reason}]");
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Deterministically resolves .NET wiring to its target(s): no source bodies.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="service">A service to resolve to its implementation.</param>
    /// <param name="request">A request/command to resolve to its handler.</param>
    /// <param name="route">A route to resolve to its action.</param>
    /// <param name="config">A config section to resolve to its options type.</param>
    /// <param name="symbol">A symbol to resolve to its declaration.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The resolved target(s) with paths and evidence.</returns>
    [McpServerTool(Name = "fuse_resolve", ReadOnly = true)]
    [Description("Deterministically resolve .NET wiring: a service to its implementation, a request to its handler, a route to its action, a config section to its options, or a symbol to its declaration. No source bodies.")]
    public static async Task<string> FuseResolveAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("A service to resolve to its implementation.")] string? service = null,
        [Description("A request/command to resolve to its handler.")] string? request = null,
        [Description("A route to resolve, for example \"POST /api/orders/{id}\".")] string? route = null,
        [Description("A config section to resolve to its options type.")] string? config = null,
        [Description("A symbol name to resolve to its declaration.")] string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var resolver = new SemanticResolver(store);

        ResolveResult? result = null;
        if (!string.IsNullOrWhiteSpace(service))
            result = await resolver.ResolveServiceAsync(service, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(request))
            result = await resolver.ResolveRequestAsync(request, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(route))
            result = await resolver.ResolveRouteAsync(route, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(config))
            result = await resolver.ResolveConfigAsync(config, cancellationToken);
        else if (!string.IsNullOrWhiteSpace(symbol))
            result = await resolver.ResolveSymbolAsync(symbol, cancellationToken);

        if (result is null)
            return "Error: specify one of service, request, route, config, or symbol.";

        var builder = new StringBuilder();
        builder.AppendLine($"resolve {result.Target.ToString().ToLowerInvariant()}: {result.Query}");
        if (result.Matches.Count == 0)
            builder.AppendLine("  no matches");
        foreach (var match in result.Matches)
        {
            var location = match.FilePath is null ? string.Empty : $"  ({match.FilePath}:{match.StartLine})";
            builder.AppendLine($"  [{match.Relation}] {match.Kind} {match.DisplayName}{location}");
            if (match.Signature is not null)
                builder.AppendLine($"      {match.Signature}");
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Plans and emits context for a set of seeds, with source bodies at mixed render tiers.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="sessionStore">The session store used to elide unchanged files.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="seeds">Symbol seeds.</param>
    /// <param name="files">File path seeds (for example paths from localize).</param>
    /// <param name="services">Service seeds to resolve and expand.</param>
    /// <param name="requests">Request/command seeds to resolve and expand.</param>
    /// <param name="configs">Config section seeds to resolve and expand.</param>
    /// <param name="routes">Route seeds.</param>
    /// <param name="depth">The graph expansion depth.</param>
    /// <param name="maxTokens">The token budget.</param>
    /// <param name="format">The output format: xml, markdown, or json.</param>
    /// <param name="sessionId">Session id; files already sent unchanged in the session are elided.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The emitted context payload.</returns>
    [McpServerTool(Name = "fuse_context", ReadOnly = true)]
    [Description("Plan and emit context (source bodies, mixed render tiers, manifest, provenance) for a set of seeds. Feed it the file paths from fuse_localize or the names from fuse_resolve. Pass a sessionId to elide files already sent in the session.")]
    public static async Task<string> FuseContextAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        ContextSessionStore sessionStore,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Symbol seeds.")] string[]? seeds = null,
        [Description("File path seeds (for example the paths returned by fuse_localize).")] string[]? files = null,
        [Description("Service seeds to resolve and expand.")] string[]? services = null,
        [Description("Request/command seeds to resolve and expand.")] string[]? requests = null,
        [Description("Config section seeds to resolve and expand.")] string[]? configs = null,
        [Description("Route seeds, for example \"POST /api/orders/{id}\".")] string[]? routes = null,
        [Description("Graph expansion depth.")] int depth = 2,
        [Description("Token budget; must-keep seeds are always included.")] int maxTokens = 0,
        [Description("Output format: xml (default), markdown, or json.")] string format = "xml",
        [Description("Session id; files already sent unchanged in this session are elided.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var seedList = BuildSeeds(seeds, files, services, requests, configs, routes);
        if (seedList.Count == 0)
            return "Error: provide at least one seed (symbol/file/service/request/config/route).";

        var root = Path.GetFullPath(path);
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store);
        var plan = await engine.PlanContextAsync(
            new ContextRequest(root, seedList, depth, maxTokens > 0 ? maxTokens : null), cancellationToken);

        var renderer = new SemanticContextRenderer(reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, cancellationToken);
        var unchanged = string.IsNullOrWhiteSpace(sessionId) ? null : sessionStore.Reconcile(sessionId, rendered.Files);
        return SemanticContextEmitter.Emit(plan, rendered, ParseFormat(format), root, unchangedPaths: unchanged);
    }

    private static List<ContextSeed> BuildSeeds(string[]? seeds, string[]? files, string[]? services, string[]? requests, string[]? configs, string[]? routes) =>
        (seeds ?? []).Select(s => new ContextSeed(ContextSeedKind.Symbol, s))
            .Concat((files ?? []).Select(f => new ContextSeed(ContextSeedKind.File, f)))
            .Concat((services ?? []).Select(s => new ContextSeed(ContextSeedKind.Service, s)))
            .Concat((requests ?? []).Select(r => new ContextSeed(ContextSeedKind.Request, r)))
            .Concat((configs ?? []).Select(c => new ContextSeed(ContextSeedKind.Config, c)))
            .Concat((routes ?? []).Select(r => new ContextSeed(ContextSeedKind.Route, r)))
            .ToList();

    /// <summary>
    ///     Reviews the semantic impact of a change and emits the packed context.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="changeSource">The change source for resolving the git base ref.</param>
    /// <param name="sessionStore">The session store used to elide unchanged files.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="changedSince">The git base ref to diff against.</param>
    /// <param name="maxTokens">The token budget.</param>
    /// <param name="includeTests">Whether to include related test files.</param>
    /// <param name="format">The output format: xml, markdown, or json.</param>
    /// <param name="sessionId">Session id; files already sent unchanged in the session are elided.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The review preamble plus the emitted context payload.</returns>
    [McpServerTool(Name = "fuse_review", ReadOnly = true)]
    [Description("Review the semantic impact of a change since a git base ref: changed files, the blast radius (callers, DI consumers, route/request handlers, options consumers, tests), and the packed context. The flagship tool for PR/change work.")]
    public static async Task<string> FuseReviewAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        IChangeSource changeSource,
        ContextSessionStore sessionStore,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The git base ref to diff against (branch, commit, or HEAD~N).")] string changedSince = "HEAD",
        [Description("Token budget; changed files are always kept.")] int maxTokens = 0,
        [Description("Include related test files.")] bool includeTests = true,
        [Description("Output format: xml (default), markdown, or json.")] string format = "xml",
        [Description("Session id; files already sent unchanged in this session are elided.")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store, changeSource);
        var plan = await engine.ReviewAsync(
            new ReviewRequest(root, changedSince, MaxTokens: maxTokens > 0 ? maxTokens : null, IncludeTests: includeTests),
            cancellationToken);

        var renderer = new SemanticContextRenderer(reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, cancellationToken);
        var unchanged = string.IsNullOrWhiteSpace(sessionId) ? null : sessionStore.Reconcile(sessionId, rendered.Files);
        return SemanticContextEmitter.Emit(plan, rendered, ParseFormat(format), root, changedSince, unchanged);
    }

    private static ContextOutputFormat ParseFormat(string format) => format.Trim().ToLowerInvariant() switch
    {
        "markdown" or "md" => ContextOutputFormat.Markdown,
        "json" => ContextOutputFormat.Json,
        _ => ContextOutputFormat.Xml,
    };
}
