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
    ///     Batch exact-signature lookup: for a set of symbol names, returns each declared signature, kind,
    ///     accessibility, and location from the semantic index in one call. The compiler-shaped answer to the
    ///     agent's most common question ("what is the exact shape of this member"), replacing many grep-and-read
    ///     round-trips.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="names">The symbol names to look up (simple or fully qualified).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limitPerName">The maximum matches to return per requested name.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The signatures grouped by requested name, with a note for any name that did not match.</returns>
    [McpServerTool(Name = "fuse_signatures", ReadOnly = true)]
    [Description("Batch exact signatures for named symbols from the semantic index, in one call instead of many grep-and-read round-trips. Returns the declared signature, kind, accessibility, and location per match. A signature is available at the semantic tier; in syntax mode it may be absent, and the tool says so rather than inventing one.")]
    public static async Task<string> FuseSignaturesAsync(
        SemanticIndexer indexer,
        [Description("Symbol names to look up (simple name or fully qualified).")] string[] names,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Maximum matches to return per requested name.")] int limitPerName = 5,
        CancellationToken cancellationToken = default)
    {
        if (names is null || names.Length == 0)
            return "Error: provide one or more symbol names in 'names'.";

        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var matches = await store.GetSignaturesByNamesAsync(names, limitPerName, cancellationToken);

        var builder = new StringBuilder();
        foreach (var requested in names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal))
        {
            var forName = matches
                .Where(m => string.Equals(m.Name, requested, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(m.FullyQualifiedName, requested, StringComparison.OrdinalIgnoreCase))
                .ToList();
            builder.AppendLine($"# {requested}");
            if (forName.Count == 0)
            {
                builder.AppendLine("  no match in the index (check the name, or run fuse_index if the file is new).");
                continue;
            }

            foreach (var m in forName)
            {
                var accessibility = string.IsNullOrEmpty(m.Accessibility) ? "" : m.Accessibility + " ";
                var signature = string.IsNullOrEmpty(m.Signature)
                    ? $"{m.Kind} {m.FullyQualifiedName} (no signature recorded; index is syntax-tier for this file)"
                    : $"{accessibility}{m.Signature}";
                var container = string.IsNullOrEmpty(m.ContainingType) ? "" : $" in {m.ContainingType}";
                builder.AppendLine($"  {signature}{container}");
                builder.AppendLine($"    {m.FilePath}:{m.StartLine} [{m.Kind}{(m.IsPublicApi ? ", public-api" : "")}]");
            }
        }

        return builder.ToString().TrimEnd();
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
    ///     Blast radius for a symbol before an edit: the callers, implementers, consumers, and referencing types a
    ///     change to it would touch, enumerated from the persisted semantic graph (R5's reference edges plus the
    ///     wiring edges). The precise signature-change break set requires an oracle-grade load and is reported as
    ///     unavailable otherwise, per the availability contract.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="symbol">The symbol whose blast radius to compute.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="limit">The maximum impacted items to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The impacted files and symbols with the edge that connects them, plus an availability note.</returns>
    [McpServerTool(Name = "fuse_impact", ReadOnly = true)]
    [Description("Blast radius for a symbol before you edit it: the callers, implementers, consumers, and referencing types a change would touch, from the persisted semantic graph. No bodies. The exact signature-change break set (which call sites would no longer bind) needs an oracle-grade (tier-1) load and is reported unavailable otherwise, rather than guessed.")]
    public static async Task<string> FuseImpactAsync(
        SemanticIndexer indexer,
        [Description("The symbol (simple or qualified name) whose blast radius to compute.")] string symbol,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Maximum impacted items to return.")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return "Error: provide a symbol name.";

        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
        var explorer = new GraphNeighborhoodExplorer(store);
        var impact = await explorer.CallersAndImplementersAsync(symbol, limit, cancellationToken);

        var builder = new StringBuilder();
        builder.AppendLine($"impact of {symbol}: {impact.Count} impacted (index mode {mode})");
        foreach (var item in impact)
        {
            var symbolPart = item.Symbol is null ? string.Empty : $"  {item.Symbol}";
            builder.AppendLine($"  {item.Path}{symbolPart}  [{item.Reason}]");
        }

        if (impact.Count == 0 && mode == "syntax")
            builder.AppendLine("  (no edges: syntax mode has no semantic graph; run fuse_index on a semantically loadable checkout)");

        // Availability contract: the exact signature-change break set is an oracle-grade answer (a bind-check
        // against a resident compilation). No tier-1 load exists yet, so it is reported unavailable, not guessed.
        builder.AppendLine();
        builder.AppendLine(
            "signature-change break set: unavailable (needs an oracle-grade tier-1 load; the enumeration above is " +
            "the graph-grade blast radius from the persisted reference and wiring edges).");

        return builder.ToString();
    }

    /// <summary>
    ///     Compiler-executed solution-wide rename (R7): renames a symbol and every reference through Roslyn and
    ///     returns the change as a staged diff, never touching the working tree. Answers only when the whole
    ///     solution loads (a partial rename is worse than none) and abstains otherwise.
    /// </summary>
    /// <param name="path">The workspace directory.</param>
    /// <param name="symbol">The simple name of the symbol to rename.</param>
    /// <param name="newName">The new name.</param>
    /// <param name="cancellationToken">A token to cancel the rename.</param>
    /// <returns>The staged per-file diffs, or an explicit abstention.</returns>
    [McpServerTool(Name = "fuse_refactor", ReadOnly = true)]
    [Description("Compiler-executed solution-wide rename: rename a symbol and all its references through Roslyn, returned as a staged diff (nothing is written to disk). Roslyn semantics mean a same-named unrelated symbol is not touched. Answers only when the whole solution loads cleanly; abstains otherwise, because a partial rename is worse than none. Review the diff and re-check with fuse_check before applying.")]
    public static async Task<string> FuseRefactorAsync(
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The simple name of the symbol to rename.")] string symbol = "",
        [Description("The new name.")] string newName = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol) || string.IsNullOrWhiteSpace(newName))
            return "Error: provide the symbol to rename and the new name.";

        var root = Path.GetFullPath(path);
        var discovery = await new Fuse.Semantics.DotNetWorkspaceDiscoverer().DiscoverAsync(root, cancellationToken);
        var target = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (target is null)
            return "cannot rename: no solution or project found. fuse_refactor abstains.";

        var result = await new Fuse.Semantics.RenameRefactorer().RenameAsync(target, symbol, newName, cancellationToken);
        if (!result.Renamed)
            return $"cannot rename: {result.Reason}";

        var builder = new StringBuilder();
        builder.AppendLine($"staged rename: {result.OldName} -> {result.NewName} ({result.Diffs.Count} file(s) changed, not written to disk)");
        foreach (var d in result.Diffs)
        {
            builder.AppendLine($"--- {d.FilePath}");
            builder.AppendLine(d.UnifiedDiff);
        }

        builder.AppendLine();
        builder.AppendLine("Review this diff and re-check with fuse_check before applying; a rename crossing a boundary Roslyn does not see (a string, reflection) would surface as a diagnostic there.");
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     Speculatively typechecks a proposed single-file edit against the build-captured compilation (R1): the
    ///     compiler diagnostics the change would produce, without writing the file or running <c>dotnet build</c>.
    ///     Answers only at oracle-grade (tier-1) load and abstains otherwise, per the availability contract.
    /// </summary>
    /// <param name="path">The workspace directory.</param>
    /// <param name="file">The repo-relative path of the file being changed.</param>
    /// <param name="content">The proposed full new content of that file.</param>
    /// <param name="cancellationToken">A token to cancel the check.</param>
    /// <returns>The diagnostics for the changed document, a clean verdict, or an explicit abstention.</returns>
    [McpServerTool(Name = "fuse_check", ReadOnly = true)]
    [Description("Speculatively typecheck a proposed single-file edit: the compiler errors and warnings it would produce, without writing the file or running dotnet build. Oracle-grade: answers only when the repo builds and is captured at tier-1, and abstains with a reason otherwise rather than guessing green.")]
    public static async Task<string> FuseCheckAsync(
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The repo-relative path of the file being changed.")] string file = "",
        [Description("The proposed full new content of that file.")] string content = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrEmpty(content))
            return "Error: provide the changed file path and its proposed new content.";

        var root = Path.GetFullPath(path);
        var client = new Fuse.Semantics.BuildCaptureClient();
        if (!client.IsAvailable)
            return "cannot verify: oracle-grade build capture is not configured (set FUSE_BUILD_CAPTURE_WORKER). fuse_check abstains rather than guessing green.";

        var discovery = await new Fuse.Semantics.DotNetWorkspaceDiscoverer().DiscoverAsync(root, cancellationToken);
        var buildTarget = discovery.SolutionPath ?? discovery.ProjectPaths.FirstOrDefault();
        if (buildTarget is null)
            return "cannot verify: no solution or project found to build. fuse_check abstains.";

        var result = await client.CheckAsync(buildTarget, file, content, TimeSpan.FromMinutes(10), cancellationToken);
        if (!result.Verified)
            return $"cannot verify: {result.Reason}";

        if (result.IsClean)
            return $"clean: no errors in the changed document {file} (speculative typecheck, no build, no disk write).";

        var builder = new StringBuilder();
        builder.AppendLine($"diagnostics for {file} (speculative typecheck): {result.Diagnostics.Count}");
        foreach (var d in result.Diagnostics)
            builder.AppendLine($"  {d.Severity} {d.Id} at line {d.Line}: {d.Message}");
        return builder.ToString().TrimEnd();
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
