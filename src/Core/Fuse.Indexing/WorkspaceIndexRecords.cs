namespace Fuse.Indexing;

/// <summary>
///     A file tracked by the index. Identity is the normalized path; the integer <c>file_id</c> is
///     assigned by the store and preserved across re-index via upsert.
/// </summary>
/// <param name="Path">The file path as discovered (may be absolute or repo-relative).</param>
/// <param name="NormalizedPath">The canonical path used as the natural key and for cross-record linkage.</param>
/// <param name="Extension">The file extension including the leading dot.</param>
/// <param name="SizeBytes">The file size in bytes.</param>
/// <param name="MtimeUtcTicks">The last-write time in UTC ticks.</param>
/// <param name="ContentHash">A hash of the file content used for change detection.</param>
/// <param name="ProjectPath">The owning project path, resolved to <c>project_id</c>, or null.</param>
/// <param name="IsGenerated">Whether the file is generated.</param>
/// <param name="IsTest">Whether the file is a test file.</param>
/// <param name="IndexedAtUtc">When the file was indexed; defaults to now at insert.</param>
public sealed record IndexedFileRecord(
    string Path,
    string NormalizedPath,
    string Extension,
    long SizeBytes,
    long MtimeUtcTicks,
    string ContentHash,
    string? ProjectPath = null,
    bool IsGenerated = false,
    bool IsTest = false,
    DateTimeOffset? IndexedAtUtc = null);

/// <summary>
///     A project tracked by the index. Identity is the project path.
/// </summary>
/// <param name="Path">The project file path.</param>
/// <param name="Name">The project name.</param>
/// <param name="ProjectHash">A hash of the project state used for change detection.</param>
/// <param name="AssemblyName">The output assembly name, when known.</param>
/// <param name="TargetFramework">The target framework moniker, when known.</param>
/// <param name="IndexedAtUtc">When the project was indexed; defaults to now at insert.</param>
public sealed record ProjectRecord(
    string Path,
    string Name,
    string ProjectHash,
    string? AssemblyName = null,
    string? TargetFramework = null,
    DateTimeOffset? IndexedAtUtc = null);

/// <summary>
///     A node in the semantic graph: a symbol, route, service, config section, file, or chunk.
/// </summary>
/// <param name="NodeId">The stable node identifier (primary key).</param>
/// <param name="Kind">The node kind (for example <c>type</c>, <c>method</c>, <c>route</c>, <c>service</c>).</param>
/// <param name="DisplayName">A human-readable name for the node.</param>
/// <param name="StableKey">A stable key independent of source position, used for cross-run matching.</param>
/// <param name="FilePath">The owning file path, resolved to <c>file_id</c>, or null.</param>
/// <param name="ProjectPath">The owning project path, resolved to <c>project_id</c>, or null.</param>
/// <param name="SymbolId">The associated symbol id, when the node wraps a symbol.</param>
/// <param name="StartLine">The 1-based start line, when positioned in source.</param>
/// <param name="EndLine">The 1-based end line, when positioned in source.</param>
/// <param name="Signature">The node signature, when applicable.</param>
/// <param name="MetadataJson">Free-form JSON metadata, when applicable.</param>
public sealed record NodeRecord(
    string NodeId,
    string Kind,
    string DisplayName,
    string StableKey,
    string? FilePath = null,
    string? ProjectPath = null,
    string? SymbolId = null,
    int? StartLine = null,
    int? EndLine = null,
    string? Signature = null,
    string? MetadataJson = null);

/// <summary>
///     A declared symbol: a type, member, or other named source element.
/// </summary>
/// <param name="SymbolId">The stable symbol identifier (primary key).</param>
/// <param name="FilePath">The declaring file path, resolved to <c>file_id</c>.</param>
/// <param name="Kind">The symbol kind.</param>
/// <param name="Name">The simple name.</param>
/// <param name="FullyQualifiedName">The fully qualified name.</param>
/// <param name="MetadataName">The metadata name, when known.</param>
/// <param name="ContainingType">The containing type's fully qualified name, when nested.</param>
/// <param name="Namespace">The containing namespace, when known.</param>
/// <param name="AssemblyName">The declaring assembly name, when known.</param>
/// <param name="Accessibility">The declared accessibility, when known.</param>
/// <param name="Signature">The full signature, when applicable.</param>
/// <param name="StartLine">The 1-based start line.</param>
/// <param name="EndLine">The 1-based end line.</param>
/// <param name="IsPublicApi">Whether the symbol is part of the public API surface.</param>
/// <param name="ProjectPath">The owning project path, resolved to <c>project_id</c>, or null.</param>
public sealed record SymbolRecord(
    string SymbolId,
    string FilePath,
    string Kind,
    string Name,
    string FullyQualifiedName,
    string? MetadataName = null,
    string? ContainingType = null,
    string? Namespace = null,
    string? AssemblyName = null,
    string? Accessibility = null,
    string? Signature = null,
    int StartLine = 0,
    int EndLine = 0,
    bool IsPublicApi = false,
    string? ProjectPath = null);

/// <summary>
///     A unit of source text used for full-text search and reduced rendering.
/// </summary>
/// <param name="ChunkId">The stable chunk identifier (primary key).</param>
/// <param name="FilePath">The owning file path, resolved to <c>file_id</c>.</param>
/// <param name="Kind">The chunk kind (for example <c>type</c>, <c>method</c>, <c>config</c>).</param>
/// <param name="StableKey">A stable key independent of source position.</param>
/// <param name="StartLine">The 1-based start line.</param>
/// <param name="EndLine">The 1-based end line.</param>
/// <param name="TextHash">A hash of the chunk text used for change detection.</param>
/// <param name="TokenEstimate">The estimated token count of the full chunk text.</param>
/// <param name="ReducedTokenEstimate">The estimated token count after reduction.</param>
/// <param name="SymbolId">The associated symbol id, when the chunk wraps a symbol.</param>
/// <param name="Name">The chunk name, when applicable.</param>
/// <param name="Signature">The chunk signature, when applicable.</param>
/// <param name="Outline">A structural outline of the chunk, when applicable.</param>
/// <param name="Body">The chunk body text indexed into full-text search; not stored relationally.</param>
/// <param name="Comments">Comment text indexed into full-text search; not stored relationally.</param>
/// <param name="SymbolsText">Referenced symbol names indexed into full-text search; not stored relationally.</param>
public sealed record ChunkRecord(
    string ChunkId,
    string FilePath,
    string Kind,
    string StableKey,
    int StartLine,
    int EndLine,
    string TextHash,
    int TokenEstimate,
    int ReducedTokenEstimate,
    string? SymbolId = null,
    string? Name = null,
    string? Signature = null,
    string? Outline = null,
    string? Body = null,
    string? Comments = null,
    string? SymbolsText = null);

/// <summary>
///     A dense embedding vector for one chunk, persisted so a prose query can be ranked by meaning without
///     re-embedding the corpus at query time.
/// </summary>
/// <param name="ChunkId">The chunk this vector embeds.</param>
/// <param name="Dimension">The number of components in <paramref name="Vector" />.</param>
/// <param name="Vector">The unit-length embedding vector.</param>
public sealed record ChunkEmbeddingRecord(
    string ChunkId,
    int Dimension,
    float[] Vector);

/// <summary>
///     A persisted chunk embedding joined to its file, returned for dense retrieval.
/// </summary>
/// <param name="ChunkId">The chunk id.</param>
/// <param name="FilePath">The chunk's file, normalized path.</param>
/// <param name="Name">The chunk's symbol or declaration name, when known.</param>
/// <param name="Vector">The unit-length embedding vector.</param>
public sealed record ChunkEmbedding(
    string ChunkId,
    string FilePath,
    string? Name,
    float[] Vector);

/// <summary>
///     A typed, weighted edge between two semantic nodes.
/// </summary>
/// <param name="FromNodeId">The source node id.</param>
/// <param name="ToNodeId">The target node id.</param>
/// <param name="EdgeType">The edge type (for example <c>di_resolves_to</c>, <c>route_handles</c>).</param>
/// <param name="Weight">The traversal weight.</param>
/// <param name="Confidence">The extraction confidence.</param>
/// <param name="Evidence">A human-readable evidence string, when available.</param>
/// <param name="EvidenceFilePath">The file path the evidence came from, resolved to <c>evidence_file_id</c>.</param>
/// <param name="EvidenceStartLine">The 1-based evidence start line, when positioned.</param>
/// <param name="EvidenceEndLine">The 1-based evidence end line, when positioned.</param>
/// <param name="MetadataJson">Free-form JSON metadata, when applicable.</param>
public sealed record SemanticEdgeRecord(
    string FromNodeId,
    string ToNodeId,
    string EdgeType,
    double Weight,
    double Confidence,
    string? Evidence = null,
    string? EvidenceFilePath = null,
    int? EvidenceStartLine = null,
    int? EvidenceEndLine = null,
    string? MetadataJson = null);

/// <summary>
///     An HTTP route mapped to a handler.
/// </summary>
/// <param name="RouteId">The stable route identifier (primary key).</param>
/// <param name="HttpMethod">The HTTP method (for example <c>GET</c>, <c>POST</c>).</param>
/// <param name="RoutePattern">The route pattern (for example <c>/api/orders/{id}</c>).</param>
/// <param name="FilePath">The declaring file path, resolved to <c>file_id</c>.</param>
/// <param name="StartLine">The 1-based start line.</param>
/// <param name="EndLine">The 1-based end line.</param>
/// <param name="SourceKind">How the route was discovered (for example <c>mvc</c>, <c>minimal-api</c>).</param>
/// <param name="HandlerSymbolId">The handler symbol id, when resolved.</param>
/// <param name="MetadataJson">Free-form JSON metadata, when applicable.</param>
public sealed record RouteRecord(
    string RouteId,
    string HttpMethod,
    string RoutePattern,
    string FilePath,
    int StartLine,
    int EndLine,
    string SourceKind,
    string? HandlerSymbolId = null,
    string? MetadataJson = null);

/// <summary>
///     A dependency-injection registration of a service and its implementation.
/// </summary>
/// <param name="RegistrationId">The stable registration identifier (primary key).</param>
/// <param name="ServiceName">The registered service type name.</param>
/// <param name="Lifetime">The service lifetime (for example <c>Scoped</c>, <c>Singleton</c>).</param>
/// <param name="FilePath">The declaring file path, resolved to <c>file_id</c>.</param>
/// <param name="StartLine">The 1-based start line.</param>
/// <param name="EndLine">The 1-based end line.</param>
/// <param name="RegistrationKind">How the registration was expressed (for example <c>generic</c>, <c>typeof</c>).</param>
/// <param name="Confidence">The extraction confidence.</param>
/// <param name="ServiceSymbolId">The service symbol id, when resolved.</param>
/// <param name="ImplementationSymbolId">The implementation symbol id, when resolved.</param>
/// <param name="ImplementationName">The implementation type name, when known.</param>
/// <param name="Evidence">A human-readable evidence string, when available.</param>
public sealed record DiRegistrationRecord(
    string RegistrationId,
    string ServiceName,
    string Lifetime,
    string FilePath,
    int StartLine,
    int EndLine,
    string RegistrationKind,
    double Confidence,
    string? ServiceSymbolId = null,
    string? ImplementationSymbolId = null,
    string? ImplementationName = null,
    string? Evidence = null);

/// <summary>
///     A binding between a configuration section and an options type.
/// </summary>
/// <param name="BindingId">The stable binding identifier (primary key).</param>
/// <param name="OptionsName">The options type name.</param>
/// <param name="FilePath">The declaring file path, resolved to <c>file_id</c>.</param>
/// <param name="StartLine">The 1-based start line.</param>
/// <param name="EndLine">The 1-based end line.</param>
/// <param name="BindingKind">How the binding was expressed (for example <c>configure</c>, <c>bind</c>, <c>get</c>).</param>
/// <param name="Confidence">The extraction confidence.</param>
/// <param name="OptionsSymbolId">The options symbol id, when resolved.</param>
/// <param name="ConfigSection">The configuration section name, when known.</param>
/// <param name="Evidence">A human-readable evidence string, when available.</param>
public sealed record OptionsBindingRecord(
    string BindingId,
    string OptionsName,
    string FilePath,
    int StartLine,
    int EndLine,
    string BindingKind,
    double Confidence,
    string? OptionsSymbolId = null,
    string? ConfigSection = null,
    string? Evidence = null);
