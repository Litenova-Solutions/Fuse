using Fuse.Collection;
using Fuse.Collection.FileSystem;
using Fuse.Collection.Filters;
using Fuse.Indexing;
using Fuse.Semantics;
using Fuse.Semantics.Analyzers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Semantics.Tests;

// N4 tier-1: the indexer's build-capture write path ingests the worker's graph bundle into the store. Tested
// with a synthetic capture (the worker-to-parent round-trip is covered separately) so the ingest is exercised
// deterministically without spawning a build.
public sealed class IndexFromCaptureTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-capture-ingest", Guid.NewGuid().ToString("N"));
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), "fuse-capture-ingest-db", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "OrderService.cs"),
            "namespace App; public interface IOrderService { } public sealed class OrderService : IOrderService { }");
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Ingest_writes_the_bundle_symbols_nodes_and_edges_to_the_store()
    {
        var files = new List<IndexedFileRecord>
        {
            new("OrderService.cs", "OrderService.cs", ".cs", 120, 1, "h1", Language: "csharp"),
        };
        var capture = CaptureResult.Ok(
        [
            new CapturedProject(
                Name: "App", FilePath: "App.csproj", AssemblyName: "App", ErrorCount: 0, TypeCount: 2,
                Symbols:
                [
                    new SymbolRecord("symbol:App.OrderService", "OrderService.cs", "type", "OrderService", "App.OrderService", StartLine: 1, EndLine: 1, IsPublicApi: true),
                ],
                Nodes:
                [
                    new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "OrderService.cs"),
                    new NodeRecord("type:App.OrderService", "type", "OrderService", "App.OrderService", "OrderService.cs"),
                ],
                Edges:
                [
                    new SemanticEdgeRecord("type:App.IOrderService", "type:App.OrderService", "di_resolves_to", 0.95, 0.95, EvidenceFilePath: "OrderService.cs"),
                ],
                Routes: [], DiRegistrations: [], OptionsBindings: []),
        ]);

        var result = await CreateIndexer().IndexFromCaptureAsync(_root, _store, files, capture, CancellationToken.None);

        Assert.Equal("semantic", result.Mode);
        Assert.Equal(1, result.SymbolCount);
        var state = await _store.GetStateAsync(CancellationToken.None);
        Assert.Equal(1, state.SymbolCount);
        var edges = await _store.GetAllEdgesAsync(CancellationToken.None);
        Assert.Contains(edges, e => e.EdgeType == "di_resolves_to" && e.FromNodeId == "type:App.IOrderService");
    }

    private static SemanticIndexer CreateIndexer()
    {
        var fileSystem = new PhysicalFileSystem();
        var pipeline = new FileCollectionPipeline(
            fileSystem,
            new GitIgnoreParser(fileSystem),
            [new GitIgnoreFilter(), new ExtensionFilter(), new ExcludedDirectoryFilter(), new EmptyFileFilter(), new BinaryFileFilter(fileSystem)]);
        return new SemanticIndexer(
            new DotNetWorkspaceDiscoverer(),
            new RoslynWorkspaceLoader(),
            new WorkspaceFileScanner(pipeline, new FileHashService()),
            new SemanticSymbolExtractor(),
            new SyntaxSymbolExtractor(),
            new SyntaxRouteExtractor(),
            new FileHashService(),
            SemanticAnalysisRunner.CreateDefault());
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, Path.GetDirectoryName(_databasePath)! })
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
