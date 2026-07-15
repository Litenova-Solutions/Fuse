using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

public sealed class SymbolGraphStoreIntegrationTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-graph-port-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexConnectionFactory _factory = null!;
    private SymbolGraphStore _graph = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _factory = new WorkspaceIndexConnectionFactory(_databasePath);
        var fts = new FtsSearchEngine(_factory);
        _graph = new SymbolGraphStore(_factory, fts);

        await using var connection = await _factory.OpenAsync(CancellationToken.None);
        await new IndexSchemaMigrator(_factory).PrepareDatabaseAsync(connection, CancellationToken.None);
        await IndexSchemaMigrator.MigrateAsync(connection, CancellationToken.None);
        await IndexSchemaMigrator.EnsureTablesAsync(connection, CancellationToken.None);
        await fts.TryCreateAsync(connection, CancellationToken.None);
    }

    [Fact]
    public async Task UpsertSymbols_and_ListSymbols_round_trip()
    {
        await _graph.UpsertFilesAsync(
            [new IndexedFileRecord("src/A.cs", "src/A.cs", ".cs", 50, 1, "h1")],
            CancellationToken.None);
        await _graph.UpsertSymbolsAsync(
            [new SymbolRecord("symbol:A.Foo", "src/A.cs", "type", "Foo", "A.Foo", StartLine: 1, EndLine: 5)],
            CancellationToken.None);

        var symbols = await _graph.ListSymbolsAsync(10, CancellationToken.None);

        Assert.Contains(symbols, s => s.Name == "Foo" && s.FilePath == "src/A.cs");
    }

    [Fact]
    public async Task UpsertEdges_and_GetOutgoingEdges_round_trip()
    {
        await _graph.UpsertFilesAsync(
            [new IndexedFileRecord("src/A.cs", "src/A.cs", ".cs", 50, 1, "h1")],
            CancellationToken.None);
        await _graph.UpsertNodesAsync(
            [
                new NodeRecord("node:a", "type", "A", "A", "src/A.cs"),
                new NodeRecord("node:b", "type", "B", "B", "src/A.cs"),
            ],
            CancellationToken.None);
        await _graph.UpsertEdgesAsync(
            [new SemanticEdgeRecord("node:a", "node:b", "references", 1.0, 1.0, EvidenceFilePath: "src/A.cs")],
            CancellationToken.None);

        var edges = await _graph.GetOutgoingEdgesAsync("node:a", CancellationToken.None);

        Assert.Contains(edges, e => e.ToNodeId == "node:b" && e.EdgeType == "references");
    }

    public Task DisposeAsync()
    {
        _factory.ClearPool();
        return Task.CompletedTask;
    }
}
