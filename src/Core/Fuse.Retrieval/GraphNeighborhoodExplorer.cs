using Fuse.Indexing;

namespace Fuse.Retrieval;

/// <summary>
///     First-class, deterministic navigation primitives for iterative exploration: the graph neighborhood of a
///     file, the callers and implementers of a symbol, and the structurally central files of an area. Each is
///     ranked, bounded, and body-free, with provenance, so an agent can chain several cheap calls into a strong
///     few-call funnel rather than reading file after file.
/// </summary>
/// <remarks>
///     Built over the language-agnostic node, edge, and file tables, so the primitives carry to any indexed
///     language. They expose the same typed graph that resolution and review use; where the graph is sparse
///     (syntax mode) the neighborhood falls back to same-folder cohesion so the result is never empty. Co-change
///     neighbors are not part of this neighborhood view; the co-change signal is applied in the open-ended scorer.
/// </remarks>
public sealed class GraphNeighborhoodExplorer
{
    private readonly IWorkspaceIndexStore _store;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GraphNeighborhoodExplorer" /> class.
    /// </summary>
    /// <param name="store">The index store to traverse.</param>
    public GraphNeighborhoodExplorer(IWorkspaceIndexStore store) => _store = store;

    /// <summary>
    ///     Returns the neighborhood of a file: the files its declared types connect to through typed edges
    ///     (callers, implementers, consumers, configuration), plus same-folder cohesion as a fallback, ranked and
    ///     bounded, with the edge or relation that brought each in.
    /// </summary>
    /// <param name="filePath">The seed file's path (normalized or raw).</param>
    /// <param name="limit">The maximum neighbors to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The ranked neighbor items, never including the seed file itself.</returns>
    public async Task<IReadOnlyList<ExploredItem>> NeighborhoodAsync(
        string filePath, int limit, CancellationToken cancellationToken)
    {
        var seed = Normalize(filePath);
        var items = new Dictionary<string, ExploredItem>(StringComparer.Ordinal);

        foreach (var node in await _store.GetNodesByFileAsync(seed, cancellationToken))
        {
            foreach (var edge in await _store.GetOutgoingEdgesAsync(node.NodeId, cancellationToken))
                await AddNeighborAsync(items, seed, edge.ToNodeId, $"-> {edge.EdgeType}", cancellationToken);
            foreach (var edge in await _store.GetIncomingEdgesAsync(node.NodeId, cancellationToken))
                await AddNeighborAsync(items, seed, edge.FromNodeId, $"<- {edge.EdgeType}", cancellationToken);
        }

        // Same-folder cohesion fills out a sparse (syntax-mode) neighborhood so the result is never empty.
        var folder = FolderOf(seed);
        if (folder.Length > 0)
        {
            foreach (var file in await _store.FindFilesByPathAsync(folder, limit * 2, cancellationToken))
            {
                var path = Normalize(file.NormalizedPath);
                if (path != seed && FolderOf(path) == folder && !items.ContainsKey(path))
                    items[path] = new ExploredItem(path, null, file.Extension, "same folder");
            }
        }

        return items.Values
            .OrderByDescending(i => i.Reason.StartsWith("same folder", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(i => i.Path, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    ///     Returns the callers and implementers of a symbol: the nodes that point at it through incoming edges
    ///     (a consumer that injects it, a type that implements it, a request whose handler it is).
    /// </summary>
    /// <param name="symbol">The symbol display name to resolve.</param>
    /// <param name="limit">The maximum results to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The ranked caller and implementer items.</returns>
    public async Task<IReadOnlyList<ExploredItem>> CallersAndImplementersAsync(
        string symbol, int limit, CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, ExploredItem>(StringComparer.Ordinal);
        foreach (var node in await _store.FindNodesByDisplayNameAsync(SimpleName(symbol), cancellationToken))
        {
            foreach (var edge in await _store.GetIncomingEdgesAsync(node.NodeId, cancellationToken))
            {
                var source = await _store.GetNodeAsync(edge.FromNodeId, cancellationToken);
                if (source?.FilePath is null)
                    continue;
                var key = source.NodeId;
                if (!items.ContainsKey(key))
                    items[key] = new ExploredItem(Normalize(source.FilePath), source.DisplayName, source.Kind, $"<- {edge.EdgeType}");
            }
        }

        return items.Values
            .OrderBy(i => i.Path, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    ///     Returns the tests that cover a symbol: the test types that reach it through an incoming <c>tests</c>
    ///     edge (R5). Because R5's <c>tests</c> edges are DI-resolved (a test injecting <c>IOrderService</c>
    ///     carries an edge to the registered <c>OrderService</c>), the covering set follows the wiring, not just
    ///     the literal type name. This is the M1 covering-test selection primitive: the small subset an agent can
    ///     run with its own <c>dotnet test --filter</c> instead of the whole suite. It is best-effort, never "all
    ///     the tests": a test reached only through reflection or a source generator has no edge and is not
    ///     selected, so the set is a lower bound bounded by R5's edge completeness.
    /// </summary>
    /// <param name="symbol">The symbol display name whose covering tests to select.</param>
    /// <param name="limit">The maximum tests to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The covering test items (the test type and its file), or empty when no <c>tests</c> edge reaches the symbol.</returns>
    public async Task<IReadOnlyList<ExploredItem>> CoveringTestsAsync(
        string symbol, int limit, CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, ExploredItem>(StringComparer.Ordinal);
        foreach (var node in await _store.FindNodesByDisplayNameAsync(SimpleName(symbol), cancellationToken))
        {
            foreach (var edge in await _store.GetIncomingEdgesAsync(node.NodeId, cancellationToken))
            {
                // Only tests edges select a covering test; a caller or implementer is blast radius, not coverage.
                if (!string.Equals(edge.EdgeType, "tests", StringComparison.Ordinal))
                    continue;
                var source = await _store.GetNodeAsync(edge.FromNodeId, cancellationToken);
                if (source?.FilePath is null)
                    continue;
                items.TryAdd(source.NodeId, new ExploredItem(Normalize(source.FilePath), source.DisplayName, source.Kind, "covers"));
            }
        }

        return items.Values
            .OrderBy(i => i.Path, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    ///     Returns the structurally central files of an area (a folder prefix, or the whole workspace when empty):
    ///     the files whose declared types have the highest node degree in the semantic graph.
    /// </summary>
    /// <param name="areaPrefix">A path prefix to scope to, or empty for the whole workspace.</param>
    /// <param name="limit">The maximum files to return.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The most central files in the area, highest degree first; empty in syntax mode (no edges).</returns>
    public async Task<IReadOnlyList<ExploredItem>> CentralFilesAsync(
        string areaPrefix, int limit, CancellationToken cancellationToken)
    {
        var edges = await _store.GetAllEdgesAsync(cancellationToken);
        if (edges.Count == 0)
            return [];

        var nodeDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            nodeDegree[edge.FromNodeId] = nodeDegree.GetValueOrDefault(edge.FromNodeId) + 1;
            nodeDegree[edge.ToNodeId] = nodeDegree.GetValueOrDefault(edge.ToNodeId) + 1;
        }

        var prefix = areaPrefix.Replace('\\', '/').Trim('/');
        var fileDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (nodeId, degree) in nodeDegree)
        {
            var node = await _store.GetNodeAsync(nodeId, cancellationToken);
            var path = node?.FilePath is null ? null : Normalize(node.FilePath);
            if (path is null || (prefix.Length > 0 && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (degree > fileDegree.GetValueOrDefault(path))
                fileDegree[path] = degree;
        }

        return fileDegree
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(kv => new ExploredItem(kv.Key, null, "file", $"central (degree {kv.Value})"))
            .ToList();
    }

    private async Task AddNeighborAsync(
        Dictionary<string, ExploredItem> items, string seed, string neighborNodeId, string reason, CancellationToken cancellationToken)
    {
        var node = await _store.GetNodeAsync(neighborNodeId, cancellationToken);
        if (node?.FilePath is null)
            return;
        var path = Normalize(node.FilePath);
        if (path == seed || items.ContainsKey(path))
            return;
        items[path] = new ExploredItem(path, node.DisplayName, node.Kind, reason);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string FolderOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    private static string SimpleName(string name)
    {
        var trimmed = name.Trim();
        var lastDot = trimmed.LastIndexOf('.');
        return lastDot >= 0 ? trimmed[(lastDot + 1)..] : trimmed;
    }
}

/// <summary>
///     A single exploration result: a file (and optionally a symbol within it) reached during navigation, with
///     the relation that brought it in and no source body.
/// </summary>
/// <param name="Path">The file's normalized path.</param>
/// <param name="Symbol">The symbol display name when the item is a node, or null for a file-level item.</param>
/// <param name="Kind">The node or file kind.</param>
/// <param name="Reason">The edge or relation that surfaced this item (for example <c>&lt;- di_injects</c> or <c>same folder</c>).</param>
public sealed record ExploredItem(string Path, string? Symbol, string Kind, string Reason);
