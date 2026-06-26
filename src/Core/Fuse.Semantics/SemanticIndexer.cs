using Fuse.Indexing;

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
    public SemanticIndexer(
        DotNetWorkspaceDiscoverer discoverer,
        RoslynWorkspaceLoader loader,
        WorkspaceFileScanner scanner,
        SemanticSymbolExtractor semanticSymbols,
        SyntaxSymbolExtractor syntaxSymbols,
        SyntaxRouteExtractor routeExtractor,
        FileHashService hashService)
    {
        _discoverer = discoverer;
        _loader = loader;
        _scanner = scanner;
        _semanticSymbols = semanticSymbols;
        _syntaxSymbols = syntaxSymbols;
        _routeExtractor = routeExtractor;
        _hashService = hashService;
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

        var (chunks, routes) = await ExtractChunksAndRoutesAsync(root, files, dropChunkSymbolIds: true, cancellationToken);
        await store.UpsertChunksAsync(chunks, cancellationToken);
        await store.UpsertRoutesAsync(routes, cancellationToken);

        // Any load diagnostic (MSBuild warning, a project without a compilation) means the semantic picture is
        // incomplete; report that honestly as partial rather than claiming a clean semantic index.
        var mode = snapshot.Diagnostics.Any(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
            ? "partial"
            : "semantic";

        return new SemanticIndexResult(mode, linkedFiles.Count, projects.Count, symbols.Count, chunks.Count, routes.Count, snapshot.Diagnostics);
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

        return new SemanticIndexResult("syntax", files.Count, 0, symbols.Count, chunks.Count, routes.Count, snapshot.Diagnostics);
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
