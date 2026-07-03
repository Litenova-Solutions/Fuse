using System.ComponentModel;
using System.Text;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Plugins.Rerank.Onnx;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP tool definitions for Fuse V3, exposed to AI agents through the Model Context Protocol server.
/// </summary>
/// <remarks>
///     Each method maps to an MCP tool whose name is set by <see cref="McpServerToolAttribute" /> (for example
///     <c>fuse_resolve</c>). The thirteen tools (index, map, localize, resolve, context, review, find, neighbors,
///     signatures, impact, check, refactor, reduce) work over the persistent semantic index; read tools build the
///     index on first use. Tools return errors as descriptive strings rather than throwing.
/// </remarks>
[McpServerToolType]
public sealed partial class FuseTools
{
    /// <summary>
    ///     Builds or refreshes the persistent semantic index for a workspace.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="cancellationToken">A token to cancel indexing.</param>
    /// <returns>A summary of the index pass, or a descriptive error.</returns>
    [McpServerTool(Name = "fuse_index", ReadOnly = false)]
    [Description("Build or refresh the persistent semantic index for a .NET workspace. Run once before the read tools, or to pick up changes. Returns a summary (mode, files, projects, symbols, routes).")]
    public static async Task<string> FuseIndexAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            return $"Error: Directory not found: {root}";

        await EnsureDenseModelAsync(cancellationToken);
        await using var store = await OpenStoreAsync(root, cancellationToken);
        var result = await indexer.IndexAsync(root, store, cancellationToken);
        return $"Indexed [{result.Mode}] {result.FileCount} files, {result.ProjectCount} projects, " +
               $"{result.SymbolCount} symbols, {result.ChunkCount} chunks, {result.RouteCount} routes.";
    }

    /// <summary>
    ///     Prints a map of the indexed workspace (symbols, routes, counts).
    /// </summary>
    /// <param name="indexer">The semantic indexer (used to build the index on first use).</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="detail">The detail to include: symbols, routes, or all.</param>
    /// <param name="maxRows">The maximum rows per section.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The workspace map.</returns>
    [McpServerTool(Name = "fuse_map", ReadOnly = true)]
    [Description("Print a map of the workspace: indexed symbols, routes, and counts. The cheap first call to understand structure before fetching context.")]
    public static async Task<string> FuseMapAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Detail to include: symbols, routes, or all. Default: all.")] string detail = "all",
        [Description("Maximum rows per section.")] int maxRows = 200,
        CancellationToken cancellationToken = default)
    {
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var renderer = new WorkspaceMapRenderer(store);
        return await renderer.RenderAsync(ParseDetail(detail), maxRows, cancellationToken);
    }

    /// <summary>
    ///     Exact lookup over the index: symbols by name, files by path, and chunks by full-text.
    /// </summary>
    /// <param name="indexer">The semantic indexer (used to build the index on first use).</param>
    /// <param name="query">The name, path fragment, or text to find.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="kind">Restrict to one kind: symbol, path, text, or all.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The matches grouped by kind.</returns>
    [McpServerTool(Name = "fuse_find", ReadOnly = true)]
    [Description("Exact lookup: find a symbol by name, a file by path fragment, or text by full-text search. Use instead of broad grep when the name or path is known.")]
    public static async Task<string> FuseFindAsync(
        SemanticIndexer indexer,
        [Description("The name, path fragment, or text to find.")] string query,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("Restrict to one kind: symbol, path, text, or all. Default: all.")] string kind = "all",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: provide a query to find.";

        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var normalizedKind = kind.Trim().ToLowerInvariant();
        var builder = new StringBuilder();

        if (normalizedKind is "all" or "symbol")
        {
            var symbols = await store.FindSymbolsByNameAsync(query, 50, cancellationToken);
            builder.AppendLine($"symbols ({symbols.Count}):");
            foreach (var symbol in symbols)
                builder.AppendLine($"  {symbol.Kind} {symbol.FullyQualifiedName}  ({symbol.FilePath}:{symbol.StartLine})");
        }

        if (normalizedKind is "all" or "path")
        {
            var files = await store.FindFilesByPathAsync(query, 50, cancellationToken);
            builder.AppendLine($"paths ({files.Count}):");
            foreach (var file in files)
                builder.AppendLine($"  {file.NormalizedPath}");
        }

        if (normalizedKind is "all" or "text")
        {
            var hits = await store.SearchAsync(new SearchQuery(query, 50), cancellationToken);
            builder.AppendLine($"text ({hits.Count}):");
            foreach (var hit in hits)
                builder.AppendLine($"  {hit.Name ?? hit.Kind}  ({hit.FilePath}:{hit.StartLine})");
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Compacts a specific set of files (or raw content) without collecting a whole directory.
    /// </summary>
    /// <param name="orchestrator">The fusion orchestrator.</param>
    /// <param name="templateRegistry">The project template registry.</param>
    /// <param name="path">Base directory for resolving relative file paths.</param>
    /// <param name="files">Explicit file paths to reduce.</param>
    /// <param name="content">Raw content to reduce instead of files.</param>
    /// <param name="extension">The extension selecting the reducer for content.</param>
    /// <param name="level">The reduction level.</param>
    /// <param name="maxTokens">The token ceiling, or zero for none.</param>
    /// <param name="cancellationToken">A token to cancel the run.</param>
    /// <returns>The reduced output, or a descriptive error.</returns>
    [McpServerTool(Name = "fuse_reduce", ReadOnly = true)]
    [Description("Compact a specific set of files (or raw content) by running Fuse's reduction, without collecting a whole directory. Pass `files` or `content` (+ `extension`).")]
    public static Task<string> FuseReduceAsync(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        [Description("Base directory for resolving relative file paths. Ignored in content mode.")] string path = ".",
        [Description("File paths to reduce, absolute or relative to path.")] string[]? files = null,
        [Description("Raw content to reduce instead of files. Provide extension to select the reducer.")] string? content = null,
        [Description("Extension that selects the reducer for content (for example .cs, .ts, .py). Defaults to .cs.")] string extension = ".cs",
        [Description("Reduction level: none, standard, aggressive, skeleton, publicApi. Defaults to standard.")] ReductionLevel level = ReductionLevel.Standard,
        [Description("Maximum tokens the reduced output may use, or 0 for no limit.")] int maxTokens = 0,
        CancellationToken cancellationToken = default)
    {
        int? maxTokenLimit = maxTokens > 0 ? maxTokens : null;

        if (!string.IsNullOrEmpty(content))
            return ReduceRunner.ReduceContentAsync(orchestrator, templateRegistry, content, extension, level, maxTokenLimit, cancellationToken);

        if (files is { Length: > 0 })
            return ReduceRunner.ReduceFilesAsync(orchestrator, templateRegistry, path, files, level, maxTokenLimit, cancellationToken);

        return Task.FromResult("Error: provide either files (paths) or content to reduce.");
    }

    // Opens the index store for a workspace without indexing.
    private static async Task<WorkspaceIndexStore> OpenStoreAsync(string root, CancellationToken cancellationToken)
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        var store = new WorkspaceIndexStore(databasePath);
        await store.InitializeAsync(cancellationToken);
        return store;
    }

    /// <summary>
    ///     Whether a cold read serves the syntax tier first and upgrades to the semantic graph in the background.
    ///     Enabled only by the long-lived <c>mcp serve</c> host (which owns the background task's lifetime); a
    ///     short-lived in-process caller indexes synchronously so no background task outlives it.
    /// </summary>
    public static bool BackgroundSemanticUpgradeEnabled { get; set; }

    /// <summary>
    ///     Owns the background semantic-upgrade jobs' lifetime (N3, finding 5): deduped per root, failures logged,
    ///     and cancelled and drained on host shutdown so none is orphaned. The serve host sets this to a
    ///     supervisor with a stderr sink and disposes it on shutdown; the default keeps a plain instance so a
    ///     short-lived in-process caller behaves and tests can drive it directly.
    /// </summary>
    public static SemanticUpgradeSupervisor UpgradeSupervisor { get; set; } = new();

    // Opens the store and builds the index on first use, so read tools work without an explicit fuse_index call.
    // In the long-lived serve host, cold start serves the syntax tier in a few seconds, then upgrades to the
    // semantic graph in the background so the first read does not block on the MSBuild load. In a short-lived
    // in-process caller the index is built synchronously, so no background task outlives the call.
    private static async Task<WorkspaceIndexStore> OpenIndexedAsync(SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(path);
        var store = await OpenStoreAsync(root, cancellationToken);
        var state = await store.GetStateAsync(cancellationToken);
        if (state.FileCount == 0)
        {
            await EnsureDenseModelAsync(cancellationToken);
            if (BackgroundSemanticUpgradeEnabled)
            {
                await indexer.IndexSyntaxFirstAsync(root, store, cancellationToken);
                ScheduleSemanticUpgrade(indexer, root);
            }
            else
            {
                await indexer.IndexAsync(root, store, cancellationToken);
            }
        }
        else
        {
            // Freshness contract (N6): a warm store may have been built at first call and then gone stale as the
            // agent edited files. Reconcile dirty known files (edited or deleted) before answering, so no read
            // tool serves silently stale data. A bulk change degrades to a stale-as-of stamp (see the reconciler),
            // which the availability header and fuse doctor report; it never silently serves the old index.
            await indexer.ReconcileDirtyFilesAsync(root, store, cancellationToken);
        }
        return store;
    }

    // The ambient availability header (R3): one line that tells a client the grade of the answer that follows,
    // so an oracle read is never mistaken for oracle-grade when it is not. It reports the index mode (semantic,
    // partial, or syntax), whether tier-1 build capture is configured (the oracle-grade write path), and the
    // freshness stamp from the N6 reconcile contract (a nonzero stale count means a bulk change outran the
    // per-read reconcile, so the graph may lag the working tree). Store-backed oracle tools prepend it; the
    // compiler tools (fuse_check, fuse_refactor) carry their own explicit "cannot verify/rename" abstention,
    // which is the same signal at higher resolution.
    internal static async Task<string> OracleAvailabilityHeaderAsync(WorkspaceIndexStore store, CancellationToken cancellationToken)
    {
        var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
        var staleRaw = await store.GetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, cancellationToken);
        var tier1 = new Fuse.Semantics.BuildCaptureClient().IsAvailable ? "configured" : "not configured";
        var freshness = int.TryParse(staleRaw, out var stale) && stale > 0
            ? $"{stale} known file(s) changed since index, results may lag the working tree"
            : "up to date";
        return $"availability: index mode {mode}; tier-1 build capture {tier1}; {freshness}.";
    }

    // Runs the semantic upgrade in the background on its own store handle (the foreground store is disposed when
    // the tool returns), supervised by UpgradeSupervisor: deduped per root, cancellable, its failure logged not
    // swallowed, and drained on host shutdown so no task is orphaned (N3, finding 5).
    private static void ScheduleSemanticUpgrade(SemanticIndexer indexer, string root)
    {
        UpgradeSupervisor.Schedule(root, async cancellationToken =>
        {
            var databasePath = FuseStorePaths.ResolveDatabasePath(root);
            await using var store = new WorkspaceIndexStore(databasePath);
            await store.InitializeAsync(cancellationToken);
            await indexer.UpgradeToSemanticAsync(root, store, cancellationToken);
        });
    }


    // Provisions the bundled dense model once (fetch-and-cache) before the first index, so the dense channel is
    // on by default. Idempotent and offline-safe: a present model is a no-op, a failed fetch falls back to lexical.
    private static Task EnsureDenseModelAsync(CancellationToken cancellationToken) =>
        DenseModelProvisioner.EnsureModelAsync(progress: null, logger: null, cancellationToken);

    private static MapDetail ParseDetail(string detail) => detail.Trim().ToLowerInvariant() switch
    {
        "symbols" => MapDetail.Symbols,
        "routes" => MapDetail.Routes,
        _ => MapDetail.All,
    };
}
