using System.ComponentModel;
using System.Text;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP tool definitions for Fuse V3, exposed to AI agents through the Model Context Protocol server.
/// </summary>
/// <remarks>
///     Each method maps to an MCP tool whose name is set by <see cref="McpServerToolAttribute" /> (for example
///     <c>fuse_resolve</c>). The fifteen tools (index, map, localize, resolve, context, review, find, neighbors,
///     signatures, impact, check, test, refactor, changeset, reduce) work over the persistent semantic index;
///     read tools build the index on first use. Tools return errors as descriptive strings rather than throwing.
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
    ///     The workspace status and lifecycle tool (U1): the single entry point for the state of the workspace and
    ///     its index. <c>action=status</c> reports the index mode, verify grade, and freshness; <c>index</c> builds
    ///     or refreshes the index; <c>map</c> prints the symbol and route map; <c>doctor</c> diagnoses the semantic
    ///     load per project. The read actions fold the former <c>fuse_index</c> and <c>fuse_map</c>; the explicit
    ///     apply-diff write path (Decision D2) is added as a later action.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="action">The action: status, index, map, or doctor.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="detail">For the map action: the detail to include (symbols, routes, all).</param>
    /// <param name="maxRows">For the map action: the maximum rows per section.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The action's result, or a descriptive error.</returns>
    [McpServerTool(Name = "fuse_workspace", ReadOnly = false)]
    [Description("Workspace status and lifecycle (the loop's first stop). action=status (default): the index mode, verification grade, and freshness. action=index: build or refresh the persistent semantic index. action=map: the symbols, routes, and counts. action=doctor: the per-project semantic-load diagnosis (why a project loaded at the tier it did). The read actions fold fuse_index and fuse_map; the explicit apply-diff write path is added separately.")]
    public static async Task<string> FuseWorkspaceAsync(
        SemanticIndexer indexer,
        [Description("The action: status, index, map, or doctor.")] string action = "status",
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("For the map action: detail to include (symbols, routes, all).")] string detail = "all",
        [Description("For the map action: maximum rows per section.")] int maxRows = 200,
        CancellationToken cancellationToken = default)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "index":
                return await FuseIndexAsync(indexer, path, cancellationToken);
            case "map":
                return await FuseMapAsync(indexer, path, detail, maxRows, cancellationToken);
            case "doctor":
                return await WorkspaceDoctorAsync(indexer, path, cancellationToken);
            case "status":
            case "":
                return await WorkspaceStatusAsync(indexer, path, cancellationToken);
            default:
                return $"Error: unknown workspace action '{action}'. Use status, index, map, or doctor.";
        }
    }

    // The status action: the availability header (index mode, verify grade, freshness) plus the index counts.
    private static async Task<string> WorkspaceStatusAsync(SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            return $"Error: Directory not found: {root}";

        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
        var builder = new StringBuilder();
        builder.AppendLine(await OracleAvailabilityHeaderAsync(store, root, cancellationToken));
        builder.AppendLine($"workspace: {root}");
        builder.AppendLine($"index mode: {mode}");
        return builder.ToString().TrimEnd();
    }

    // The doctor action: the per-project semantic-load diagnosis, so a downgrade names its reason per project.
    private static async Task<string> WorkspaceDoctorAsync(SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            return $"Error: Directory not found: {root}";

        var diagnosis = await indexer.DiagnoseLoadAsync(root, cancellationToken);
        var builder = new StringBuilder();
        builder.AppendLine($"workspace: {root}");
        builder.AppendLine($"load tier: {diagnosis.Tier}");
        builder.AppendLine($"projects loaded: {diagnosis.ProjectsLoaded}/{diagnosis.ProjectsTotal}");
        if (diagnosis.Projects.Count == 0)
        {
            builder.AppendLine("no projects: the workspace has no solution or project, or none opened; indexing is syntax-only.");
        }
        else
        {
            foreach (var project in diagnosis.Projects)
                builder.AppendLine($"  {project.Name}: {(project.Loaded ? "loaded" : "not loaded")} - {project.Reason}");
        }

        return builder.ToString().TrimEnd();
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

    /// <summary>
    ///     The speculative staging area for <c>fuse_changeset</c> (M1). Sessions persist across MCP calls in the
    ///     long-lived serve host, so an agent can create a changeset, stage edits, diagnose and select, then
    ///     promote or discard across separate tool calls. Held in memory, keyed by an opaque session id.
    /// </summary>
    public static Fuse.Retrieval.ChangesetSessionStore ChangesetSessions { get; } = new();

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
        else if (ResidentWorkspaces.DescribeResident(root) is null)
        {
            // Freshness contract (N6): a warm store may have been built at first call and then gone stale as the
            // agent edited files. Reconcile dirty known files (edited or deleted) before answering, so no read
            // tool serves silently stale data. A bulk change degrades to a stale-as-of stamp (see the reconciler),
            // which the availability header and fuse doctor report; it never silently serves the old index.
            //
            // Single-writer discipline (S1/D8): when a resident workspace serves this root, its watcher is the sole
            // store writer (it projects the changed cone on each edit), so the read path must NOT also reconcile -
            // two writers would race. The condition is false only when a resident provider is wired (opt-in), so
            // the default store-backed path reconciles exactly as before.
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
    /// <summary>
    ///     The resident-workspace seam (S1, Decision D8): the availability header consults this to say whether a
    ///     read is served by a live resident workspace or by the store. The default reports no resident workspace,
    ///     so a process without a wired resident engine answers store-backed exactly as before; the host that
    ///     holds a resident workspace replaces it with a provider over its live state.
    /// </summary>
    public static Fuse.Workspace.IResidentWorkspaceProvider ResidentWorkspaces { get; set; } =
        Fuse.Workspace.NullResidentWorkspaceProvider.Instance;

    internal static async Task<string> OracleAvailabilityHeaderAsync(WorkspaceIndexStore store, string root, CancellationToken cancellationToken)
    {
        var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
        var staleRaw = await store.GetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, cancellationToken);
        var tier1Available = new Fuse.Semantics.BuildCaptureClient().IsAvailable;
        var tier1 = tier1Available ? "configured" : "not configured";
        // Name the verification grade fuse_check can currently serve (T0, D11): oracle-grade when tier-1 build
        // capture is configured, otherwise the build-grade fallback (dotnet build scoped to the owning project).
        // The verify verb never shrugs where a project can be built; the grade names the latency to expect.
        var verifyGrade = tier1Available
            ? "verify serves oracle-grade"
            : "verify serves build-grade (fuse_check runs a scoped dotnet build)";
        // Name which truth answered (S1/D8): a live resident workspace (current as of its stamp) or the store.
        var resident = ResidentWorkspaces.DescribeResident(root);
        var residentClause = resident is null
            ? "store-backed"
            : $"resident ({resident.ProjectCount} project(s), current as of {resident.AsOf})";
        var freshness = int.TryParse(staleRaw, out var stale) && stale > 0
            ? $"{stale} known file(s) changed since index, results may lag the working tree"
            : "up to date";
        return $"availability: index mode {mode}; tier-1 build capture {tier1}; {verifyGrade}; workspace {residentClause}; {freshness}.";
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

    private static MapDetail ParseDetail(string detail) => detail.Trim().ToLowerInvariant() switch
    {
        "symbols" => MapDetail.Symbols,
        "routes" => MapDetail.Routes,
        _ => MapDetail.All,
    };
}
