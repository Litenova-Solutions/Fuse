using Fuse.Indexing;

namespace Fuse.Semantics;

/// <summary>
///     Indexes a workspace at the syntax level: discovers files, then extracts and stores symbols and chunks
///     for each C# file without resolving cross-file semantics.
/// </summary>
/// <remarks>
///     This is the Phase 2 batch indexer. It produces an approximate but immediately useful index (symbols,
///     chunks, full-text search) before the heavier MSBuild/Roslyn semantic pass (Phase 3) runs. Files are
///     upserted first so symbol and chunk records resolve their <c>file_id</c> by path.
/// </remarks>
public sealed class SyntaxIndexer
{
    private readonly WorkspaceFileScanner _scanner;
    private readonly IWorkspaceIndexStore _store;
    private readonly SyntaxSymbolExtractor _extractor;
    private readonly SyntaxRouteExtractor _routeExtractor;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SyntaxIndexer" /> class.
    /// </summary>
    /// <param name="scanner">The scanner used to discover files.</param>
    /// <param name="store">The index store to write records into.</param>
    /// <param name="extractor">The syntax extractor used to produce symbols and chunks.</param>
    /// <param name="routeExtractor">The syntax extractor used to produce routes.</param>
    public SyntaxIndexer(
        WorkspaceFileScanner scanner,
        IWorkspaceIndexStore store,
        SyntaxSymbolExtractor extractor,
        SyntaxRouteExtractor routeExtractor)
    {
        _scanner = scanner;
        _store = store;
        _extractor = extractor;
        _routeExtractor = routeExtractor;
    }

    /// <summary>
    ///     Indexes the workspace under a root directory.
    /// </summary>
    /// <param name="rootDirectory">The workspace root.</param>
    /// <param name="cancellationToken">A token to cancel the index.</param>
    /// <returns>A summary of how many files, symbols, and chunks were indexed.</returns>
    public async Task<SyntaxIndexResult> IndexAsync(string rootDirectory, CancellationToken cancellationToken)
    {
        var files = await _scanner.ScanAsync(new FileScanRequest(rootDirectory), cancellationToken);
        await _store.UpsertFilesAsync(files, cancellationToken);

        var symbols = new List<SymbolRecord>();
        var chunks = new List<ChunkRecord>();
        var routes = new List<RouteRecord>();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Extension != ".cs")
                continue;

            var absolutePath = Path.Combine(rootDirectory, file.Path);
            var content = await File.ReadAllTextAsync(absolutePath, cancellationToken);
            var extracted = _extractor.Extract(file.NormalizedPath, content);
            symbols.AddRange(extracted.Symbols);
            chunks.AddRange(extracted.Chunks);
            routes.AddRange(_routeExtractor.Extract(file.NormalizedPath, content));
        }

        await _store.UpsertSymbolsAsync(symbols, cancellationToken);
        await _store.UpsertChunksAsync(chunks, cancellationToken);
        await _store.UpsertRoutesAsync(routes, cancellationToken);

        return new SyntaxIndexResult(files.Count, symbols.Count, chunks.Count, routes.Count);
    }
}

/// <summary>
///     A summary of a syntax indexing pass.
/// </summary>
/// <param name="FileCount">The number of files indexed.</param>
/// <param name="SymbolCount">The number of symbols extracted and stored.</param>
/// <param name="ChunkCount">The number of chunks extracted and stored.</param>
/// <param name="RouteCount">The number of routes extracted and stored.</param>
public sealed record SyntaxIndexResult(int FileCount, int SymbolCount, int ChunkCount, int RouteCount);
