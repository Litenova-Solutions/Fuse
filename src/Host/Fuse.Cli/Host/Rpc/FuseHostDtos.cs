namespace Fuse.Cli.Rpc;

/// <summary>
///     The result of the <c>fuse/handshake</c> method: the host's package version, the wire protocol version
///     the UI client must match, and the session token required on every subsequent RPC call. A protocol
///     mismatch is surfaced to the user as a clear notification rather than failing later with an opaque
///     serialization error.
/// </summary>
/// <param name="HostVersion">The host package version (for example <c>3.0.0</c>).</param>
/// <param name="ProtocolVersion">The RPC protocol version; the client compares it to its own.</param>
/// <param name="SessionToken">The session token the client must pass on all RPC methods except handshake.</param>
public sealed record FuseHostHandshake(string HostVersion, int ProtocolVersion, string SessionToken);

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

/// <summary>
///     One emitted file in a scope result: the path the scope included and its token cost, for the scope-result
///     tree view.
/// </summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="TokenCost">The token cost the file contributed to the emitted payload.</param>
public sealed record ScopeFileDto(string Path, int TokenCost);

/// <summary>
///     The result of the <c>fuse/scope</c> method: the files a scoped fusion included with their token costs, the
///     total token count, and a path to the emitted payload written to a temp file the extension opens read-only.
/// </summary>
/// <param name="Mode">The scoping mode that ran (<c>focus</c>, <c>search</c>, or <c>changes</c>).</param>
/// <param name="Files">The emitted files with their token costs, most expensive first.</param>
/// <param name="TotalTokens">The total tokens across the emitted payload.</param>
/// <param name="PayloadPath">The absolute path to the emitted payload file, or <c>null</c> when nothing emitted.</param>
public sealed record ScopeResultDto(
    string Mode,
    IReadOnlyList<ScopeFileDto> Files,
    long TotalTokens,
    string? PayloadPath);

/// <summary>
///     One secret diagnostic: a detected secret's kind and its zero-based line and character range in a file, so
///     the extension can underline the exact literal in the editor and the Problems panel. The value is shown
///     redacted in any emitted payload; this diagnostic only points at where it lives in the source.
/// </summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="Kind">The secret kind (for example <c>github-token</c>).</param>
/// <param name="StartLine">Zero-based start line.</param>
/// <param name="StartColumn">Zero-based start character.</param>
/// <param name="EndLine">Zero-based end line.</param>
/// <param name="EndColumn">Zero-based end character (exclusive).</param>
public sealed record SecretDiagnosticDto(
    string Path,
    string Kind,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

/// <summary>
///     One token hotspot: a file whose estimated token cost is high enough to flag as an informational
///     diagnostic, so the budget pressure is visible in the editor as well as the hotspots tree.
/// </summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="TokenCost">The estimated token cost of including the file.</param>
public sealed record HotspotDiagnosticDto(string Path, int TokenCost);

/// <summary>
///     The result of the <c>fuse/diagnostics</c> method: the context diagnostics for a repository root. Secrets
///     carry precise spans; hotspots flag budget-heavy files; graph gaps name files the dependency graph leaves
///     unconnected (no inbound or outbound type reference), which often indicates dead or reflection-only code.
/// </summary>
/// <param name="Secrets">The detected secrets with their precise editor ranges.</param>
/// <param name="Hotspots">The most token-expensive files, most expensive first.</param>
/// <param name="GraphGaps">Files with no inbound or outbound dependency edge.</param>
/// <param name="Generated">Files detected as generated code (EF Core migrations and model snapshots).</param>
public sealed record DiagnosticsDto(
    IReadOnlyList<SecretDiagnosticDto> Secrets,
    IReadOnlyList<HotspotDiagnosticDto> Hotspots,
    IReadOnlyList<string> GraphGaps,
    IReadOnlyList<string> Generated);

/// <summary>
///     One planned file in an explain result: why a file was included (role), at what fidelity (tier), and its
///     relevance score, so the extension's scope-result and explainer panels can show the plan without emitting.
/// </summary>
/// <param name="Path">The normalized repository-relative file path.</param>
/// <param name="Role">The file's role (for example <c>Seed</c>, <c>Dependency</c>, <c>Changed</c>).</param>
/// <param name="Tier">The reduction tier the file was planned at (for example <c>Standard</c>, <c>Skeleton</c>).</param>
/// <param name="Score">The relevance score from scoping, or <c>0</c> when not scored.</param>
public sealed record ExplainFileDto(string Path, string Role, string Tier, double Score);

/// <summary>
///     The result of the <c>fuse/explain</c> method: the scoped result's context plan (which files a fusion
///     would include, their roles, tiers, and scores) computed without writing a payload.
/// </summary>
/// <param name="Mode">The scoping mode that produced the plan (<c>focus</c>, <c>search</c>, or <c>changes</c>).</param>
/// <param name="Files">The planned files, in plan order.</param>
public sealed record ExplainResultDto(string Mode, IReadOnlyList<ExplainFileDto> Files);
