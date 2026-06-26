using Fuse.Indexing;
using Fuse.Plugins.Abstractions.Scoping;
using Fuse.Semantics.Analyzers;

namespace Fuse.Semantics;

/// <summary>
///     The top-level workspace indexer: discovers the workspace, loads it through MSBuild/Roslyn, and writes
///     project records, files (linked to projects), symbols, chunks, and routes to the index. Falls back to
///     syntax-only indexing when semantic loading is unavailable.
/// </summary>
/// <remarks>
///     Symbols come from the semantic extractor (stable assembly-qualified ids) when the workspace loads
///     semantically, and from the syntax extractor otherwise. Chunks and routes are always produced from
///     syntax so full-text search works in both modes. The resulting mode (<c>semantic</c>, <c>partial</c>,
///     or <c>syntax</c>) is stored in the index metadata and surfaced through the store state.
/// </remarks>
public sealed class SemanticIndexer
{
    private readonly DotNetWorkspaceDiscoverer _discoverer;
    private readonly RoslynWorkspaceLoader _loader;
    private readonly WorkspaceFileScanner _scanner;
    private readonly SemanticSymbolExtractor _semanticSymbols;
    private readonly SyntaxSymbolExtractor _syntaxSymbols;
    private readonly SyntaxRouteExtractor _routeExtractor;
    private readonly FileHashService _hashService;
    private readonly SemanticAnalysisRunner _analysisRunner;
    private readonly ITextEmbedder? _embedder;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SemanticIndexer" /> class.
    /// </summary>
    /// <param name="discoverer">The workspace discoverer.</param>
    /// <param name="loader">The MSBuild/Roslyn workspace loader.</param>
    /// <param name="scanner">The file scanner.</param>
    /// <param name="semanticSymbols">The semantic symbol extractor.</param>
    /// <param name="syntaxSymbols">The syntax symbol and chunk extractor (used for chunks and as the fallback).</param>
    /// <param name="routeExtractor">The syntax route extractor.</param>
    /// <param name="hashService">The content hash service, used for project hashes.</param>
    /// <param name="analysisRunner">The semantic analyzer runner producing graph edges (semantic mode only).</param>
    /// <param name="embedder">An optional text embedder; when available, a dense vector is persisted per chunk for dense retrieval.</param>
    public SemanticIndexer(
        DotNetWorkspaceDiscoverer discoverer,
        RoslynWorkspaceLoader loader,
        WorkspaceFileScanner scanner,
        SemanticSymbolExtractor semanticSymbols,
        SyntaxSymbolExtractor syntaxSymbols,
        SyntaxRouteExtractor routeExtractor,
        FileHashService hashService,
        SemanticAnalysisRunner analysisRunner,
        ITextEmbedder? embedder = null)
    {
        _discoverer = discoverer;
        _loader = loader;
        _scanner = scanner;
        _semanticSymbols = semanticSymbols;
        _syntaxSymbols = syntaxSymbols;
        _routeExtractor = routeExtractor;
        _hashService = hashService;
        _analysisRunner = analysisRunner;
        _embedder = embedder;
    }

    /// <summary>
    ///     Indexes a workspace into the store.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="store">The index store to write to.</param>
    /// <param name="cancellationToken">A token to cancel the index.</param>
    /// <returns>A summary including the index mode, counts, and diagnostics.</returns>
    public async Task<SemanticIndexResult> IndexAsync(
        string rootDirectory,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        var discovery = await _discoverer.DiscoverAsync(root, cancellationToken);
        var snapshot = await _loader.LoadAsync(discovery, cancellationToken);
        var files = await _scanner.ScanAsync(new FileScanRequest(root), cancellationToken);

        SemanticIndexResult result = snapshot.SemanticLoadSucceeded
            ? await IndexSemanticAsync(root, store, files, snapshot, cancellationToken)
            : await IndexSyntaxAsync(root, store, files, snapshot, cancellationToken);

        await store.SetMetaAsync("index_mode", result.Mode, cancellationToken);
        return result;
    }

    /// <summary>
    ///     Re-indexes a single changed file in place: clears that file's stored rows and re-extracts its
    ///     syntax-level data (symbols, chunks, full-text, routes), without rebuilding the whole index.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="normalizedPath">The changed file's normalized (forward-slash, repo-relative) path.</param>
    /// <param name="store">The index store to update.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of symbols re-indexed for the file (0 for a non-C# file or a deleted file).</returns>
    /// <remarks>
    ///     This updates the file's own syntax-level rows only. Cross-file semantic graph edges (DI resolution,
    ///     route handlers, MediatR and EF wiring) are computed from the whole compilation and are not
    ///     recomputed here; a full <see cref="IndexAsync" /> refreshes those. The incremental path keeps an
    ///     edit-heavy session's full-text and symbol rows current at low cost. When the file no longer exists,
    ///     its rows are cleared and nothing is re-added.
    /// </remarks>
    public async Task<int> ReindexFileAsync(
        string rootDirectory,
        string normalizedPath,
        IWorkspaceIndexStore store,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(rootDirectory);
        var absolute = Path.Combine(root, normalizedPath.Replace('/', Path.DirectorySeparatorChar));

        await store.DeleteFileDataAsync(normalizedPath, cancellationToken);
        if (!File.Exists(absolute))
            return 0;

        var info = new FileInfo(absolute);
        var content = await File.ReadAllTextAsync(absolute, cancellationToken);
        var hash = _hashService.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
        await store.UpsertFilesAsync(
            [new IndexedFileRecord(normalizedPath, normalizedPath, info.Extension, info.Length, info.LastWriteTimeUtc.Ticks, hash)],
            cancellationToken);

        if (!string.Equals(info.Extension, ".cs", StringComparison.OrdinalIgnoreCase))
            return 0;

        var extracted = _syntaxSymbols.Extract(normalizedPath, content);
        await store.UpsertSymbolsAsync(extracted.Symbols, cancellationToken);
        await store.UpsertChunksAsync(extracted.Chunks, cancellationToken);
        await store.UpsertRoutesAsync(_routeExtractor.Extract(normalizedPath, content), cancellationToken);
        return extracted.Symbols.Count;
    }

    private async Task<SemanticIndexResult> IndexSemanticAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var projects = BuildProjectRecords(snapshot, cancellationToken);
        await store.UpsertProjectsAsync(projects, cancellationToken);

        var fileToProject = BuildFileProjectMap(root, snapshot);
        var linkedFiles = files
            .Select(f => fileToProject.TryGetValue(f.NormalizedPath, out var projectPath)
                ? f with { ProjectPath = projectPath }
                : f)
            .ToList();
        await store.UpsertFilesAsync(linkedFiles, cancellationToken);

        var symbols = new List<SymbolRecord>();
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            symbols.AddRange(_semanticSymbols.Extract(project, root, cancellationToken));
        }

        await store.UpsertSymbolsAsync(symbols, cancellationToken);

        var (chunks, syntaxRoutes) = await ExtractChunksAndRoutesAsync(root, files, dropChunkSymbolIds: true, cancellationToken);
        await store.UpsertChunksAsync(chunks, cancellationToken);
        await EmbedAndPersistAsync(store, chunks, cancellationToken);
        // Syntax routes first (covers minimal APIs), then the semantic MVC routes overwrite by route id with
        // their resolved handler symbol ids.
        await store.UpsertRoutesAsync(syntaxRoutes, cancellationToken);

        // Run the analyzers over every loaded project and store the resulting graph. Nodes are upserted before
        // edges so the edge foreign keys resolve.
        var graph = RunAnalyzers(root, snapshot, cancellationToken);
        await store.UpsertNodesAsync(graph.Nodes, cancellationToken);
        await store.UpsertEdgesAsync(graph.Edges, cancellationToken);
        await store.UpsertRoutesAsync(graph.Routes, cancellationToken);
        await store.UpsertDiRegistrationsAsync(graph.DiRegistrations, cancellationToken);
        await store.UpsertOptionsBindingsAsync(graph.OptionsBindings, cancellationToken);

        // Any load diagnostic (MSBuild warning, a project without a compilation) means the semantic picture is
        // incomplete; report that honestly as partial rather than claiming a clean semantic index.
        var diagnostics = snapshot.Diagnostics.Concat(graph.Diagnostics).ToList();
        var mode = snapshot.Diagnostics.Any(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            ? "partial"
            : "semantic";

        var routeCount = syntaxRoutes.Count + graph.Routes.Count;
        return new SemanticIndexResult(mode, linkedFiles.Count, projects.Count, symbols.Count, chunks.Count, routeCount, diagnostics);
    }

    private async Task<SemanticIndexResult> IndexSyntaxAsync(
        string root,
        IWorkspaceIndexStore store,
        IReadOnlyList<IndexedFileRecord> files,
        RoslynWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await store.UpsertFilesAsync(files, cancellationToken);

        var symbols = new List<SymbolRecord>();
        var (chunks, routes) = (new List<ChunkRecord>(), new List<RouteRecord>());
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Extension != ".cs")
                continue;

            var content = await File.ReadAllTextAsync(Path.Combine(root, file.Path), cancellationToken);
            var extracted = _syntaxSymbols.Extract(file.NormalizedPath, content);
            symbols.AddRange(extracted.Symbols);
            chunks.AddRange(extracted.Chunks);
            routes.AddRange(_routeExtractor.Extract(file.NormalizedPath, content));
        }

        await store.UpsertSymbolsAsync(symbols, cancellationToken);
        await store.UpsertChunksAsync(chunks, cancellationToken);
        await store.UpsertRoutesAsync(routes, cancellationToken);
        await EmbedAndPersistAsync(store, chunks, cancellationToken);

        return new SemanticIndexResult("syntax", files.Count, 0, symbols.Count, chunks.Count, routes.Count, snapshot.Diagnostics);
    }

    // Embeds each chunk's text representation (name, signature, body) and persists the vectors, when a text
    // embedder is available. A no-op when no model is present, which keeps the no-model floor: the index still
    // builds and retrieval stays lexical. Per-chunk text is truncated so tokenization cost stays bounded.
    private async Task EmbedAndPersistAsync(IWorkspaceIndexStore store, IReadOnlyList<ChunkRecord> chunks, CancellationToken cancellationToken)
    {
        if (_embedder is null || !_embedder.IsAvailable || chunks.Count == 0)
            return;

        var embeddings = new List<ChunkEmbeddingRecord>(chunks.Count);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = BuildEmbedText(chunk);
            if (text.Length == 0)
                continue;
            var vector = _embedder.Embed(text);
            if (vector.Length == 0)
                continue;
            embeddings.Add(new ChunkEmbeddingRecord(chunk.ChunkId, vector.Length, vector));
        }

        await store.UpsertEmbeddingsAsync(embeddings, cancellationToken);
    }

    // The text a chunk is embedded from: its declared name and signature carry the most meaning, followed by a
    // bounded slice of the body. Bounded to keep the model's tokenization within its context window.
    private static string BuildEmbedText(ChunkRecord chunk)
    {
        const int maxChars = 2000;
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(chunk.Name))
            parts.Add(chunk.Name!);
        if (!string.IsNullOrWhiteSpace(chunk.Signature))
            parts.Add(chunk.Signature!);
        if (!string.IsNullOrWhiteSpace(chunk.Body))
            parts.Add(chunk.Body!);

        var text = string.Join('\n', parts).Trim();
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    // Chunks and routes always come from syntax so full-text search works in both modes. In semantic mode the
    // chunk symbol ids are dropped: they would be the syntax fallback ids, which do not match the semantic
    // symbol table, so a dangling reference is avoided.
    private async Task<(List<ChunkRecord> Chunks, List<RouteRecord> Routes)> ExtractChunksAndRoutesAsync(
        string root,
        IReadOnlyList<IndexedFileRecord> files,
        bool dropChunkSymbolIds,
        CancellationToken cancellationToken)
    {
        var chunks = new List<ChunkRecord>();
        var routes = new List<RouteRecord>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Extension != ".cs")
                continue;

            var content = await File.ReadAllTextAsync(Path.Combine(root, file.Path), cancellationToken);
            var extracted = _syntaxSymbols.Extract(file.NormalizedPath, content);
            foreach (var chunk in extracted.Chunks)
                chunks.Add(dropChunkSymbolIds ? chunk with { SymbolId = null } : chunk);
            routes.AddRange(_routeExtractor.Extract(file.NormalizedPath, content));
        }

        return (chunks, routes);
    }

    // Runs the analyzer set over every loaded project and merges the per-project graphs.
    private SemanticAnalyzerResult RunAnalyzers(string root, RoslynWorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<string, NodeRecord>(StringComparer.Ordinal);
        var edges = new List<SemanticEdgeRecord>();
        var routes = new List<RouteRecord>();
        var registrations = new List<DiRegistrationRecord>();
        var bindings = new List<OptionsBindingRecord>();
        var diagnostics = new List<DiagnosticRecord>();

        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = _analysisRunner.Run(new SemanticAnalysisContext(project, root), cancellationToken);
            foreach (var node in result.Nodes)
                nodes[node.NodeId] = node;
            edges.AddRange(result.Edges);
            routes.AddRange(result.Routes);
            registrations.AddRange(result.DiRegistrations);
            bindings.AddRange(result.OptionsBindings);
            diagnostics.AddRange(result.Diagnostics);
        }

        return new SemanticAnalyzerResult(nodes.Values.ToList(), edges, routes, registrations, bindings, diagnostics);
    }

    private List<ProjectRecord> BuildProjectRecords(RoslynWorkspaceSnapshot snapshot, CancellationToken cancellationToken)
    {
        var records = new List<ProjectRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(project.FilePath))
                continue;

            var hash = File.Exists(project.FilePath)
                ? _hashService.ComputeHash(File.ReadAllBytes(project.FilePath))
                : "0";
            records.Add(new ProjectRecord(
                Path: project.FilePath,
                Name: project.Name,
                ProjectHash: hash,
                AssemblyName: project.AssemblyName));
        }

        return records;
    }

    // Maps each source file (normalized relative path) to its owning project file path, so files can be linked
    // to projects. A file shared by multiple projects (multi-targeting) maps to the first project seen.
    private static Dictionary<string, string> BuildFileProjectMap(string root, RoslynWorkspaceSnapshot snapshot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var project in snapshot.Projects)
        {
            foreach (var tree in project.Compilation.SyntaxTrees)
            {
                if (string.IsNullOrEmpty(tree.FilePath))
                    continue;

                var normalized = Path.GetRelativePath(root, tree.FilePath).Replace(Path.DirectorySeparatorChar, '/');
                map.TryAdd(normalized, project.FilePath);
            }
        }

        return map;
    }
}

/// <summary>
///     A summary of a semantic indexing pass.
/// </summary>
/// <param name="Mode">The index mode: <c>semantic</c>, <c>partial</c>, or <c>syntax</c>.</param>
/// <param name="FileCount">The number of files indexed.</param>
/// <param name="ProjectCount">The number of projects indexed.</param>
/// <param name="SymbolCount">The number of symbols indexed.</param>
/// <param name="ChunkCount">The number of chunks indexed.</param>
/// <param name="RouteCount">The number of routes indexed.</param>
/// <param name="Diagnostics">Diagnostics gathered during loading and indexing.</param>
public sealed record SemanticIndexResult(
    string Mode,
    int FileCount,
    int ProjectCount,
    int SymbolCount,
    int ChunkCount,
    int RouteCount,
    IReadOnlyList<DiagnosticRecord> Diagnostics);
