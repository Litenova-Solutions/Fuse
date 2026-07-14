using System.ComponentModel;
using System.Text;
using Fuse.Cli;
using Fuse.Collection.Templates;
using Fuse.Fusion;
using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Options;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;
using Fuse.Semantics;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP tool definitions for Fuse, exposed to AI agents through the Model Context Protocol server.
/// </summary>
/// <remarks>
///     Each method maps to an MCP tool whose name is set by <see cref="McpServerToolAttribute" /> (for example
///     <c>fuse_find</c>). The eight loop tools (workspace, find, context, impact, check, test, refactor, review)
///     plus <c>fuse_reduce</c> work over the persistent semantic index; read tools build the index on first use.
///     Tools return errors as descriptive strings rather than throwing.
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
    // Reached through fuse_workspace (action=index); kept as an internal helper the workspace tool calls.
    public static async Task<string> FuseIndexAsync(
        SemanticIndexer indexer,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            return FuseOperationalErrors.FormatWorkspaceNotFound(root);

        var result = await IndexAccess.IndexAsync(indexer, path, cancellationToken);
        FuseMetrics.RecordIndexMode(root, result.Mode);
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
    // Reached through fuse_workspace (action=map); kept as an internal helper the workspace tool calls.
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
    ///     The workspace status and lifecycle tool: the single entry point for the state of the workspace and
    ///     its index. <c>action=status</c> reports the index mode, verify grade, and freshness; <c>index</c> builds
    ///     or refreshes the index; <c>map</c> prints the symbol and route map; <c>doctor</c> diagnoses the semantic
    ///     load per project; <c>apply</c> is the one explicit tree-write path (Decision D2), a dry run unless
    ///     <c>write=true</c>.
    /// </summary>
    /// <param name="indexer">The semantic indexer.</param>
    /// <param name="action">The action: status, index, map, or doctor.</param>
    /// <param name="path">The workspace directory.</param>
    /// <param name="detail">For the map action: the detail to include (symbols, routes, all).</param>
    /// <param name="maxRows">For the map action: the maximum rows per section.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The action's result, or a descriptive error.</returns>
    [McpServerTool(Name = "fuse_workspace", ReadOnly = false)]
    [Description("Workspace status and lifecycle (the loop's first stop). action=status (default): the index mode, verification grade, and freshness. action=index: build or refresh the persistent semantic index. action=map: the symbols, routes, and counts. action=doctor: the per-project semantic-load diagnosis. action=apply: write a proposed single-file edit (file + content) to the working tree - the one explicit apply path (Decision D2); it is a dry run that only reports the change unless write=true, and it refuses any path that escapes the workspace root.")]
    public static Task<string> FuseWorkspaceAsync(
        SemanticIndexer indexer,
        [Description("The action: status, index, map, doctor, or apply.")] string action = "status",
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("For the map action: detail to include (symbols, routes, all).")] string detail = "all",
        [Description("For the map action: maximum rows per section.")] int maxRows = 200,
        [Description("For the apply action: the repo-relative file to write.")] string file = "",
        [Description("For the apply action: the full new content to write to that file.")] string content = "",
        [Description("For the apply action: actually write (otherwise a dry run reports the change without writing).")] bool write = false,
        CancellationToken cancellationToken = default) =>
        ExecuteReadMcpAsync(() => FuseWorkspaceCoreAsync(
            indexer, action, path, detail, maxRows, file, content, write, cancellationToken));

    private static async Task<string> FuseWorkspaceCoreAsync(
        SemanticIndexer indexer,
        string action,
        string path,
        string detail,
        int maxRows,
        string file,
        string content,
        bool write,
        CancellationToken cancellationToken)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "index":
                return await FuseIndexAsync(indexer, path, cancellationToken);
            case "map":
                return await FuseMapAsync(indexer, path, detail, maxRows, cancellationToken);
            case "doctor":
                return await WorkspaceDoctorAsync(indexer, path, cancellationToken);
            case "apply":
                return await WorkspaceApplyAsync(path, file, content, write, cancellationToken);
            case "status":
            case "":
                return await WorkspaceStatusAsync(path, cancellationToken);
            default:
                return FuseOperationalErrors.Format(
                    FuseOperationalErrors.ValidationErrorPrefix,
                    $"unknown workspace action '{action}'. Use status, index, map, doctor, or apply.");
        }
    }

    // The one explicit tree-write path (Decision D2): write a single file's proposed content, guarded so the
    // server never writes outside the workspace and never writes silently. A dry run (write=false, the default)
    // reports what would change; write=true performs the one write. The path is refused if it escapes the root.
    private static async Task<string> WorkspaceApplyAsync(string path, string file, string content, bool write, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file))
            return FuseOperationalErrors.Format(
                FuseOperationalErrors.ValidationErrorPrefix,
                "apply needs a file (the repo-relative path to write) and content.");

        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            return FuseOperationalErrors.FormatWorkspaceNotFound(root);

        // Resolve and confine: GetFullPath normalizes any ../ segments; the result must stay under the root, or a
        // crafted path could escape the workspace. This is the guard the D2 write path exists to enforce.
        var full = Path.GetFullPath(Path.Combine(root, file));
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            return $"Error: refusing to write '{file}': it resolves outside the workspace root. The apply path only writes inside {root}.";

        var exists = File.Exists(full);
        if (!write)
            return $"dry run (no write): would {(exists ? "overwrite" : "create")} {file} ({content.Length} chars). Re-run with write=true to apply.";

        var dir = Path.GetDirectoryName(full);
        if (dir is not null)
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(full, content, cancellationToken);
        return $"applied: {(exists ? "overwrote" : "created")} {file} ({content.Length} chars) in the working tree.";
    }

    // The status action (R16): read-only fast path over index_meta when fuse.db exists; never auto-indexes on a
    // clean workspace. action=index remains the explicit build path.
    private static async Task<string> WorkspaceStatusAsync(string path, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            return FuseOperationalErrors.FormatWorkspaceNotFound(root);

        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return await BuildFastStatusOutputAsync(root, store: null, state: null, cancellationToken);

        await using var store = new WorkspaceIndexStore(databasePath);
        var state = await store.GetStateAsync(cancellationToken);
        return await BuildFastStatusOutputAsync(root, store, state, cancellationToken);
    }

    // The doctor action: the per-project semantic-load diagnosis, so a downgrade names its reason per project.
    // The summary header uses the same fast read-only meta path as status (R16); the MSBuild diagnosis still runs.
    private static async Task<string> WorkspaceDoctorAsync(SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            return FuseOperationalErrors.FormatWorkspaceNotFound(root);

        var builder = new StringBuilder();
        builder.AppendLine(await BuildFastDoctorSummaryHeaderAsync(root, cancellationToken));

        var diagnosis = await indexer.DiagnoseLoadAsync(root, cancellationToken);
        builder.AppendLine($"workspace: {root}");
        builder.AppendLine($"load tier: {diagnosis.Tier}");
        builder.AppendLine($"selected solution: {diagnosis.SelectedSolution ?? "none (syntax-only)"}");
        if (diagnosis.SelectionNote is not null)
            builder.AppendLine($"WARNING: {diagnosis.SelectionNote}");
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
    [Description("The find union: locate what a task needs by kind. Exact lookup - kind=symbol (by name), path (by fragment), text (full-text), or all. Wiring - kind=service, request, route, or config resolves the query to its implementation/handler/action/options. kind=signatures returns the query symbol's exact signature. kind=neighbors returns the query symbol's callers and implementers. kind=task ranks candidate files for the query with the graded refuse-and-route contract. Use instead of broad grep when the name, wiring, or task is known.")]
    public static Task<string> FuseFindAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        [Description("The name, path fragment, text, wiring identifier, or task to find.")] string query,
        [Description("Absolute or relative path to the workspace directory.")] string path = ".",
        [Description("The kind: symbol, path, text, all (exact); service, request, route, config (wiring); signatures; neighbors; task.")] string kind = "all",
        CancellationToken cancellationToken = default) =>
        ExecuteReadMcpAsync(() => FuseFindCoreAsync(
            indexer, changeSource, query, path, kind, cancellationToken));

    private static async Task<string> FuseFindCoreAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        string query,
        string path,
        string kind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return FuseOperationalErrors.Format(FuseOperationalErrors.ValidationErrorPrefix, "provide a query to find.");

        var normalizedKind = kind.Trim().ToLowerInvariant();

        // A wiring, signatures, neighbors, or task kind routes to the specialized engine logic, keyed by the query.
        switch (normalizedKind)
        {
            case "service":
                return await FuseResolveAsync(indexer, path, service: query, cancellationToken: cancellationToken);
            case "request":
                return await FuseResolveAsync(indexer, path, request: query, cancellationToken: cancellationToken);
            case "route":
                return await FuseResolveAsync(indexer, path, route: query, cancellationToken: cancellationToken);
            case "config":
                return await FuseResolveAsync(indexer, path, config: query, cancellationToken: cancellationToken);
            case "signatures":
                return await FuseSignaturesAsync(indexer, [query], path, cancellationToken: cancellationToken);
            case "neighbors":
                return await FuseNeighborsAsync(indexer, path, symbol: query, cancellationToken: cancellationToken);
            case "task":
                {
                    var taskRoot = Path.GetFullPath(path);
                    await using var taskStore = await OpenIndexedAsync(indexer, path, cancellationToken);
                    if (!taskStore.FullTextSearchAvailable)
                    {
                        return AvailabilityHeaderHelpers.FormatTaskLocalizationFtsRefusal(
                            await OracleAvailabilityHeaderAsync(taskStore, taskRoot, cancellationToken));
                    }

                    return await FuseLocalizeAsync(indexer, changeSource, path, task: query, cancellationToken: cancellationToken);
                }
        }

        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
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
        CancellationToken cancellationToken = default) =>
        FuseOperationalErrors.ExecuteMcpAsync(() => FuseReduceCoreAsync(
            orchestrator, templateRegistry, path, files, content, extension, level, maxTokens, cancellationToken));

    private static Task<string> FuseReduceCoreAsync(
        FusionOrchestrator orchestrator,
        ProjectTemplateRegistry templateRegistry,
        string path,
        string[]? files,
        string? content,
        string extension,
        ReductionLevel level,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        int? maxTokenLimit = maxTokens > 0 ? maxTokens : null;

        if (!string.IsNullOrEmpty(content))
            return ReduceRunner.ReduceContentAsync(orchestrator, templateRegistry, content, extension, level, maxTokenLimit, cancellationToken);

        if (files is { Length: > 0 })
            return ReduceRunner.ReduceFilesAsync(orchestrator, templateRegistry, path, files, level, maxTokenLimit, cancellationToken);

        return Task.FromResult(FuseOperationalErrors.Format(
            FuseOperationalErrors.ValidationErrorPrefix,
            "provide either files (paths) or content to reduce."));
    }

    // Opens the index store for a workspace without indexing (explicit write paths use ExecuteWriteAsync directly).
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

    // Opens the store and builds the index on first use, so read tools work without an explicit index call.
    // In the long-lived serve host, cold start serves the syntax tier in a few seconds, then upgrades to the
    // semantic graph in the background so the first read does not block on the MSBuild load. In a short-lived
    // in-process caller the index is built synchronously, so no background task outlives the call.
    private static async Task<WorkspaceIndexStore> OpenIndexedAsync(SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(path);
        try
        {
            var store = await IndexAccess.OpenIndexedAsync(
                indexer,
                path,
                cancellationToken);
            await RecordIndexModeAsync(store, root, cancellationToken);
            return store;
        }
        catch (Exception ex) when (IsIndexContention(ex))
        {
            throw new IndexBlockedReadException(await FormatBlockedReadHeaderAsync(root, cancellationToken));
        }
    }

    /// <summary>
    ///     Runs a read MCP tool body and returns the availability header as the tool result when the index cannot
    ///     be opened yet (R20), instead of a generic <c>index_busy:</c> prefix or an unbounded hang.
    /// </summary>
    /// <param name="action">The read tool implementation.</param>
    /// <returns>The tool result or a structured availability header on blocked read.</returns>
    internal static async Task<string> ExecuteReadMcpAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (IndexBlockedReadException ex)
        {
            return ex.AvailabilityHeader;
        }
        catch (Exception ex)
        {
            return FuseOperationalErrors.FromException(ex);
        }
    }

    private static bool IsIndexContention(Exception exception) =>
        exception is IndexBusyException
        || exception is SqliteException sqlite && sqlite.SqliteErrorCode is 5 or 6
        || exception is IOException io && io.HResult == unchecked((int)0x80070020);

    // The ambient availability header (R3): one line that tells a client the grade of the answer that follows,
    // so an oracle read is never mistaken for oracle-grade when it is not. It reports the index mode (semantic,
    // partial, or syntax), whether tier-1 build capture is configured (the oracle-grade write path), and the
    // freshness stamp from the N6 reconcile contract (a nonzero stale count means a bulk change outran the
    // per-read reconcile, so the graph may lag the working tree). Store-backed oracle tools prepend it; the
    // compiler tools (fuse_check, fuse_refactor) carry their own explicit "cannot verify/rename" abstention,
    // which is the same signal at higher resolution.
    /// <summary>
    ///     The index access seam (R19): local coordinator by default; a remote provider when MCP delegates index
    ///     writes to a shared daemon.
    /// </summary>
    public static IIndexAccessProvider IndexAccess { get; set; } = LocalIndexAccessProvider.Instance;

    /// <summary>
    ///     The resident-workspace seam (S1, Decision D8): the availability header consults this to say whether a
    ///     read is served by a live resident workspace or by the store. The default reports no resident workspace,
    ///     so a process without a wired resident engine answers store-backed exactly as before; the host that
    ///     holds a resident workspace replaces it with a provider over its live state.
    /// </summary>
    public static Fuse.Workspace.IResidentWorkspaceProvider ResidentWorkspaces { get; set; } =
        Fuse.Workspace.NullResidentWorkspaceProvider.Instance;

    internal static async Task<string> OracleAvailabilityHeaderAsync(
        WorkspaceIndexStore store, string root, CancellationToken cancellationToken, string? indexStateOverride = null)
    {
        var state = await store.GetStateAsync(cancellationToken);
        var indexState = indexStateOverride
            ?? (state.FileCount == 0 ? "not_indexed" : await ComputeIndexStateAsync(store, state, cancellationToken));
        return await FormatAvailabilityHeaderAsync(store, root, indexState, state.FileCount, cancellationToken);
    }

    internal static Task<string> FormatNotIndexedAvailabilityHeaderAsync(string root, CancellationToken cancellationToken) =>
        FormatAvailabilityHeaderAsync(store: null, root, "not_indexed", filesIndexed: 0, cancellationToken);

    internal static async Task<string> FormatBlockedReadHeaderAsync(string root, CancellationToken cancellationToken)
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return await FormatNotIndexedAvailabilityHeaderAsync(root, cancellationToken);

        try
        {
            await using var store = new WorkspaceIndexStore(databasePath);
            if (await store.OpenForReadAsync(cancellationToken) is WorkspaceIndexReadOpenStatus.Ready)
            {
                var state = await store.GetStateAsync(cancellationToken);
                return await FormatAvailabilityHeaderAsync(store, root, "index_busy", state.FileCount, cancellationToken);
            }
        }
        catch (SqliteException)
        {
        }

        return await FormatAvailabilityHeaderAsync(store: null, root, "index_busy", filesIndexed: -1, cancellationToken);
    }

    private static async Task<string> FormatAvailabilityHeaderAsync(
        WorkspaceIndexStore? store,
        string root,
        string indexState,
        int filesIndexed,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"index_state: {indexState}");
        if (filesIndexed >= 0)
            builder.AppendLine($"files_indexed: {filesIndexed}");
        builder.Append(await BuildAvailabilityLineAsync(store, root, cancellationToken));
        return builder.ToString().TrimEnd();
    }

    private static async Task<string> BuildAvailabilityLineAsync(
        WorkspaceIndexStore? store, string root, CancellationToken cancellationToken)
    {
        if (store is null)
            return BuildNotIndexedAvailabilityLine(root);

        var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
        var staleRaw = await store.GetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, cancellationToken);
        // First-use signal (C3): with the syntax-first serve default, the first reads land on a syntax index while
        // the semantic/tier-1 graph builds in the background. Name it, so a client knows a richer answer is coming
        // and that a build is running for tier-1 rather than mistaking the syntax tier for the final word.
        var pendingRaw = await store.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, cancellationToken);
        var upgradePending = pendingRaw == "1";
        var tier1Available = new BuildCaptureClient().IsAvailable;
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
        var upgradeClause = upgradePending
            ? (tier1Available
                ? " semantic upgrade in progress (a build is running for tier-1);"
                : " semantic upgrade in progress;")
            : "";
        var ftsClause = AvailabilityHeaderHelpers.FormatFtsAvailabilityClause(store.FullTextSearchAvailable);
        return $"availability: index mode {mode}; {ftsClause}; tier-1 build capture {tier1}; {verifyGrade}; workspace {residentClause};{upgradeClause} {freshness}.";
    }

    private static async Task RecordIndexModeAsync(
        WorkspaceIndexStore store, string root, CancellationToken cancellationToken)
    {
        var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
        FuseMetrics.RecordIndexMode(root, mode);
    }

    // Runs the semantic upgrade in the background on its own store handle (the foreground store is disposed when
    // the tool returns), supervised by UpgradeSupervisor: deduped per root, cancellable, its failure logged not
    // swallowed, and drained on host shutdown so no task is orphaned (N3, finding 5).
    internal static void ScheduleSemanticUpgrade(SemanticIndexer indexer, string root)
    {
        UpgradeSupervisor.Schedule(root, cancellationToken =>
            IndexCoordinator.Default.RunBackgroundUpgradeAsync(indexer, root, cancellationToken));
    }

    private static MapDetail ParseDetail(string detail) => detail.Trim().ToLowerInvariant() switch
    {
        "symbols" => MapDetail.Symbols,
        "routes" => MapDetail.Routes,
        _ => MapDetail.All,
    };

    // R16 fast status output: index_state, availability header, counts, and daemon visibility without indexing.
    private static async Task<string> BuildFastStatusOutputAsync(
        string root,
        WorkspaceIndexStore? store,
        WorkspaceIndexState? state,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        if (store is null || state is null || state.FileCount == 0)
        {
            builder.AppendLine(await FormatNotIndexedAvailabilityHeaderAsync(root, cancellationToken));
            builder.AppendLine($"workspace: {root}");
            builder.AppendLine("index mode: not_indexed");
            builder.AppendLine("files indexed: 0");
            builder.AppendLine("full-text search: unavailable");
        }
        else
        {
            builder.AppendLine(await OracleAvailabilityHeaderAsync(store, root, cancellationToken));
            builder.AppendLine($"workspace: {root}");
            builder.AppendLine($"index mode: {state.Mode ?? "unknown"}");
            builder.AppendLine($"files indexed: {state.FileCount}");
            builder.AppendLine($"full-text search: {(state.FtsAvailable ? "available" : "unavailable")}");
        }

        var daemon = await Fuse.Cli.Rpc.FuseHostClient.TryStatsAsync(root, TimeSpan.FromMilliseconds(500), cancellationToken);
        builder.AppendLine(daemon is null
            ? "daemon: none (this process serves the workspace directly)"
            : $"daemon: PID {daemon.ProcessId}, uptime {daemon.UptimeMs / 1000}s, RSS {daemon.WorkingSetBytes / (1024 * 1024)} MB (fuse host {daemon.HostVersion})");
        return builder.ToString().TrimEnd();
    }

    // R16: the doctor summary header reads index_meta only; it does not wait for semantic upgrade.
    private static async Task<string> BuildFastDoctorSummaryHeaderAsync(string root, CancellationToken cancellationToken)
    {
        var databasePath = FuseStorePaths.ResolveDatabasePath(root);
        if (!File.Exists(databasePath))
            return await FormatNotIndexedAvailabilityHeaderAsync(root, cancellationToken);

        await using var store = new WorkspaceIndexStore(databasePath);
        var state = await store.GetStateAsync(cancellationToken);
        if (state.FileCount == 0)
            return await FormatNotIndexedAvailabilityHeaderAsync(root, cancellationToken);

        return await OracleAvailabilityHeaderAsync(store, root, cancellationToken);
    }

    internal static async Task<string> ComputeIndexStateAsync(
        WorkspaceIndexStore store, WorkspaceIndexState state, CancellationToken cancellationToken)
    {
        if (state.FileCount == 0)
            return "not_indexed";

        var pending = await store.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, cancellationToken);
        if (pending == "1")
        {
            var mode = await store.GetMetaAsync("index_mode", cancellationToken) ?? "unknown";
            return mode == "syntax" ? "building_syntax" : "upgrade_pending";
        }

        var staleRaw = await store.GetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, cancellationToken);
        if (int.TryParse(staleRaw, out var stale) && stale > 0)
            return "stale_as_of";

        // R23/R31: a store with symbols but zero chunks on an FTS-available runtime is internally inconsistent
        // (search would return nothing over indexed source); never report it ready. Signal a rebuild so the read
        // path repairs it rather than serving a silent-empty "ready" index.
        if (state.FtsAvailable && state.SymbolCount > 0 && state.ChunkCount == 0)
            return "index_rebuilding";

        return "ready";
    }

    private static string BuildNotIndexedAvailabilityLine(string root)
    {
        var tier1Available = new BuildCaptureClient().IsAvailable;
        var tier1 = tier1Available ? "configured" : "not configured";
        var verifyGrade = tier1Available
            ? "verify serves oracle-grade"
            : "verify serves build-grade (fuse_check runs a scoped dotnet build)";
        var resident = ResidentWorkspaces.DescribeResident(root);
        var residentClause = resident is null
            ? "store-backed"
            : $"resident ({resident.ProjectCount} project(s), current as of {resident.AsOf})";
        return $"availability: index mode not_indexed; full-text search unavailable; tier-1 build capture {tier1}; {verifyGrade}; workspace {residentClause}; not indexed (run fuse_workspace action=index to build).";
    }
}

/// <summary>
///     Thrown when a read tool cannot open the index yet. Mapped to the structured availability header at the
///     MCP boundary (R20) instead of a bare <see cref="FuseOperationalErrors.IndexBusyPrefix" /> line.
/// </summary>
internal sealed class IndexBlockedReadException : Exception
{
    /// <summary>Initializes a new instance with the full availability header to return as the tool body.</summary>
    /// <param name="availabilityHeader">The multi-line availability header (index_state, files_indexed, availability).</param>
    public IndexBlockedReadException(string availabilityHeader)
        : base(availabilityHeader)
    {
        AvailabilityHeader = availabilityHeader;
    }

    /// <summary>The structured header to return as the MCP tool result.</summary>
    public string AvailabilityHeader { get; }
}
