using System.ComponentModel;
using Fuse.Collection.FileSystem;
using Fuse.Context;
using Fuse.Indexing;
using Fuse.Reduction;
using Fuse.Reduction.Caching;
using Fuse.Retrieval;
using Fuse.Semantics;
using ModelContextProtocol.Server;

namespace Fuse.Cli.Mcp;

/// <summary>
///     MCP resource definitions for Fuse V3, exposed to AI agents through the Model Context Protocol server.
/// </summary>
/// <remarks>
///     Each method backs an MCP resource addressed by a <c>fuse://</c> URI template. The resources mirror the
///     read workflows of the equivalent <see cref="FuseTools" /> tools over the persistent semantic index: the
///     index is built on first use, no files are written, and errors are returned as descriptive strings rather
///     than thrown. Use the tools for full control; the resources are the fixed-default addressable form.
/// </remarks>
[McpServerResourceType]
public sealed class FuseResources
{
    /// <summary>
    ///     Reads a workspace map: indexed symbols, routes, and counts. The cheap first call.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>The workspace map, or a descriptive error message when the directory is missing.</returns>
    [McpServerResource(
        UriTemplate = "fuse://map/{path}",
        Name = "Workspace Map",
        MimeType = "text/plain")]
    [Description("Returns a map of the indexed workspace (symbols, routes, counts). Mirrors fuse_workspace (action=map).")]
    public static async Task<string> ReadMapResourceAsync(
        SemanticIndexer indexer,
        [Description("Relative path to the workspace directory.")] string path,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(Path.GetFullPath(path)))
            return $"Error: Directory not found: {Path.GetFullPath(path)}";
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var renderer = new WorkspaceMapRenderer(store);
        return await renderer.RenderAsync(MapDetail.All, maxRows: 200, cancellationToken);
    }

    /// <summary>
    ///     Reads ranked candidate files and symbols for a task, with no source bodies. Mirrors fuse_find (kind=task).
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="changeSource">The change source (unused for a title-only query, passed for parity).</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="query">The task or query to localize.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>The ranked candidates, or a descriptive error message when the directory is missing.</returns>
    [McpServerResource(
        UriTemplate = "fuse://localize/{path}/{query}",
        Name = "Localized Candidates",
        MimeType = "text/plain")]
    [Description("Returns ranked candidate files and symbols for a task, no source bodies. Mirrors fuse_find (kind=task).")]
    public static async Task<string> ReadLocalizeResourceAsync(
        SemanticIndexer indexer,
        IChangeSource changeSource,
        [Description("Relative path to the workspace directory.")] string path,
        [Description("The task or query to localize.")] string query,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            return $"Error: Directory not found: {root}";
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store, changeSource);
        var result = await engine.LocalizeAsync(new LocalizationRequest(root, Query: query), cancellationToken);
        if (result.Candidates.Count == 0)
            return "No candidates found for the query.";
        return string.Join("\n", result.Candidates.Select(c =>
            $"{c.Score:F2}  {c.Path}  [{c.Kind}]  ~{c.EstimatedTokens} tok  {string.Join("; ", c.Reasons)}"));
    }

    /// <summary>
    ///     Reads planned and emitted context for a single named seed (symbol, service, request, or config).
    ///     Mirrors fuse_context.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="seed">A symbol, service, request, or config-section seed.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>The emitted context payload, or a descriptive error message when the directory is missing.</returns>
    [McpServerResource(
        UriTemplate = "fuse://context/{path}/{seed}",
        Name = "Seeded Context",
        MimeType = "text/plain")]
    [Description("Returns planned and emitted context (source bodies, manifest, provenance) for a named seed. Mirrors fuse_context.")]
    public static async Task<string> ReadContextResourceAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        [Description("Relative path to the workspace directory.")] string path,
        [Description("A symbol, service, request, or config-section seed.")] string seed,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            return $"Error: Directory not found: {root}";
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store);
        var plan = await engine.PlanContextAsync(
            new ContextRequest(root, [new ContextSeed(ContextSeedKind.Symbol, seed)]), cancellationToken);
        var renderer = new SemanticContextRenderer(reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, cancellationToken);
        return SemanticContextEmitter.Emit(plan, rendered, ContextOutputFormat.Xml, root);
    }

    /// <summary>
    ///     Reads the semantic impact of a change since a git base ref, with the packed context. Mirrors fuse_review.
    /// </summary>
    /// <param name="indexer">The semantic indexer (builds the index on first use).</param>
    /// <param name="reductionPipeline">The reduction pipeline used to render bodies.</param>
    /// <param name="changeSource">The change source for resolving the git base ref.</param>
    /// <param name="path">Relative path to the workspace directory.</param>
    /// <param name="since">Git ref (branch, commit, or <c>HEAD~N</c>) to diff against.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>The review payload, or a descriptive error message when the directory is missing.</returns>
    [McpServerResource(
        UriTemplate = "fuse://review/{path}/{since}",
        Name = "Change Review Context",
        MimeType = "text/plain")]
    [Description("Returns the semantic impact of a change since a git ref (changed files, blast radius, packed context). Mirrors fuse_review.")]
    public static async Task<string> ReadReviewResourceAsync(
        SemanticIndexer indexer,
        ContentReductionPipeline reductionPipeline,
        IChangeSource changeSource,
        [Description("Relative path to the workspace directory.")] string path,
        [Description("Git ref to diff against (branch, commit, HEAD~N).")] string since,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            return $"Error: Directory not found: {root}";
        await using var store = await OpenIndexedAsync(indexer, path, cancellationToken);
        var engine = new SemanticRetrievalEngine(store, changeSource);
        var plan = await engine.ReviewAsync(new ReviewRequest(root, since), cancellationToken);
        var renderer = new SemanticContextRenderer(reductionPipeline, new SourceContentProvider(new PhysicalFileSystem()));
        var rendered = await renderer.RenderAsync(plan, root, cancellationToken);
        return SemanticContextEmitter.Emit(plan, rendered, ContextOutputFormat.Xml, root, since);
    }

    // Opens the store and builds the index on first use, so a resource works without an explicit index call.
    private static async Task<WorkspaceIndexStore> OpenIndexedAsync(SemanticIndexer indexer, string path, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(path);
        var store = new WorkspaceIndexStore(FuseStorePaths.ResolveDatabasePath(root));
        await store.InitializeAsync(cancellationToken);
        var state = await store.GetStateAsync(cancellationToken);
        if (state.FileCount == 0)
            await indexer.IndexAsync(root, store, cancellationToken);
        else
            // Freshness contract (N6): reconcile dirty known files before serving a resource read.
            await indexer.ReconcileDirtyFilesAsync(root, store, cancellationToken);
        return store;
    }
}
