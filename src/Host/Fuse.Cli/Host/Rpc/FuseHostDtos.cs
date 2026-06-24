namespace Fuse.Cli.Rpc;

/// <summary>
///     The result of the <c>fuse/handshake</c> method: the host's package version and the wire protocol version
///     the UI client must match. A protocol mismatch is surfaced to the user as a clear notification rather than
///     failing later with an opaque serialization error.
/// </summary>
/// <param name="HostVersion">The host package version (for example <c>3.0.0</c>).</param>
/// <param name="ProtocolVersion">The RPC protocol version; the client compares it to its own.</param>
public sealed record FuseHostHandshake(string HostVersion, int ProtocolVersion);

/// <summary>
///     The result of the <c>fuse/stats</c> method: cheap process-level health for the status bar and index
///     panel. Engine statistics (token totals, cache hit rates, pattern summary) are added by the richer
///     <c>fuse/stats</c> projection in a later phase; this carries the host liveness fields that need no scoping
///     run.
/// </summary>
/// <param name="HostVersion">The host package version.</param>
/// <param name="ProcessId">The host operating-system process id.</param>
/// <param name="UptimeMs">Milliseconds since the host started serving.</param>
/// <param name="WorkingSetBytes">The host process working-set size in bytes, shown as host RSS in the UI.</param>
public sealed record FuseHostStats(string HostVersion, int ProcessId, long UptimeMs, long WorkingSetBytes);

/// <summary>
///     One node in the dependency graph projection (<c>fuse/graph</c>): a file with the data the webview needs to
///     size, color, and label it without recomputing anything.
/// </summary>
/// <param name="Path">The normalized repository-relative file path; the node identity.</param>
/// <param name="DeclaredTypes">The type names the file declares, for the node label and hover.</param>
/// <param name="Centrality">The normalized PageRank centrality in <c>[0, 1]</c>, driving node size.</param>
/// <param name="TokenCost">The estimated token cost of including the file, driving node color.</param>
/// <param name="Role">The file's role when a scope is active (seed, dependency, changed), or <c>null</c>.</param>
public sealed record GraphNodeDto(
    string Path,
    IReadOnlyList<string> DeclaredTypes,
    double Centrality,
    int TokenCost,
    string? Role);

/// <summary>
///     One directed edge in the dependency graph projection: a reference from one file to another.
/// </summary>
/// <param name="From">The referencing file path.</param>
/// <param name="To">The referenced file path.</param>
/// <param name="Weight">The edge weight (reference strength), for styling.</param>
/// <param name="Kind">The reference kind (for example <c>reference</c>), for edge styling.</param>
public sealed record GraphEdgeDto(string From, string To, double Weight, string Kind);

/// <summary>
///     The result of the <c>fuse/graph</c> method: the dependency graph at the requested level of detail.
/// </summary>
/// <param name="Nodes">The graph nodes.</param>
/// <param name="Edges">The graph edges.</param>
/// <param name="Detail">The level of detail the nodes are at (<c>Files</c> or <c>Directories</c>).</param>
public sealed record GraphDto(
    IReadOnlyList<GraphNodeDto> Nodes,
    IReadOnlyList<GraphEdgeDto> Edges,
    string Detail);

/// <summary>
///     The result of the <c>fuse/index</c> method: the warm-index state after collecting and building the graph.
/// </summary>
/// <param name="IndexState">A coarse state label (<c>Warm</c>, <c>Indexing</c>, or <c>NotIndexed</c>).</param>
/// <param name="FileCount">The number of source files indexed.</param>
/// <param name="ElapsedMs">Wall-clock milliseconds the index build took.</param>
public sealed record IndexResultDto(string IndexState, int FileCount, long ElapsedMs);
