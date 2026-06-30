using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// S5: the dependency-centrality prior nudges a tied query toward the more central file, bounded so it cannot
// promote an irrelevant file on centrality alone.
public sealed class GraphCentralityPriorTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-centrality-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await SeedAsync();
    }

    [Fact]
    public async Task CentralCandidateOutranksLeafWhenScoresTie()
    {
        var prior = new GraphCentralityPrior(_store);
        // Two candidates tied on score: the hub node is depended on by two others (high degree), the leaf has
        // no edges. The prior breaks the tie toward the hub.
        var tied = new List<ScoredCandidate>
        {
            new("type:Leaf", "src/LeafHandler.cs", "class", 0.50, [CandidateSource.FtsSymbol], ["match"], 10),
            new("type:Hub", "src/HubHandler.cs", "class", 0.50, [CandidateSource.FtsSymbol], ["match"], 10),
        };

        var ranked = await prior.ApplyAsync(tied, CancellationToken.None);

        Assert.Equal("src/HubHandler.cs", ranked[0].FilePath);
        Assert.True(ranked[0].Score > ranked[1].Score, "the central candidate should be boosted above the leaf");
    }

    [Fact]
    public async Task PriorIsBoundedAndCannotPromoteAWeakCandidate()
    {
        var prior = new GraphCentralityPrior(_store);
        // The hub is fully central but scored near zero; the leaf is weakly central but scored high. The capped
        // multiplier (at most +10 percent) cannot lift the central-but-irrelevant candidate over the strong one.
        var candidates = new List<ScoredCandidate>
        {
            new("type:Hub", "src/HubHandler.cs", "class", 0.05, [CandidateSource.FtsBody], ["weak"], 10),
            new("type:Leaf", "src/LeafHandler.cs", "class", 0.90, [CandidateSource.FtsSymbol], ["strong"], 10),
        };

        var ranked = await prior.ApplyAsync(candidates, CancellationToken.None);

        Assert.Equal("src/LeafHandler.cs", ranked[0].FilePath);
    }

    [Fact]
    public async Task PriorIsANoOpWithNoEdges()
    {
        await using var bareStore = new WorkspaceIndexStore(
            Path.Combine(Path.GetTempPath(), "fuse-centrality-bare", Guid.NewGuid().ToString("N"), "fuse.db"));
        await bareStore.InitializeAsync(CancellationToken.None);
        var prior = new GraphCentralityPrior(bareStore);
        var input = new List<ScoredCandidate>
        {
            new("type:X", "src/X.cs", "class", 0.50, [CandidateSource.FtsSymbol], ["m"], 10),
        };

        var ranked = await prior.ApplyAsync(input, CancellationToken.None);

        // No edges: scores are returned unchanged (the syntax-mode no-op).
        Assert.Equal(0.50, ranked[0].Score);
    }

    private async Task SeedAsync()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/HubHandler.cs", "src/HubHandler.cs", ".cs", 10, 1, "h1"),
                new IndexedFileRecord("src/LeafHandler.cs", "src/LeafHandler.cs", ".cs", 10, 1, "h2"),
                new IndexedFileRecord("src/ConsumerA.cs", "src/ConsumerA.cs", ".cs", 10, 1, "h3"),
                new IndexedFileRecord("src/ConsumerB.cs", "src/ConsumerB.cs", ".cs", 10, 1, "h4"),
            ],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:Hub", "src/HubHandler.cs", "type", "k1", 1, 5, "t1", 12, 8, Name: "WidgetHandler"),
                new ChunkRecord("chunk:Leaf", "src/LeafHandler.cs", "type", "k2", 1, 5, "t2", 12, 8, Name: "WidgetHandler"),
            ],
            CancellationToken.None);
        await _store.UpsertNodesAsync(
            [
                new NodeRecord("type:Hub", "class", "WidgetHandler", "App.HubHandler", "src/HubHandler.cs"),
                new NodeRecord("type:Leaf", "class", "WidgetHandler", "App.LeafHandler", "src/LeafHandler.cs"),
                new NodeRecord("type:ConsumerA", "class", "ConsumerA", "App.ConsumerA", "src/ConsumerA.cs"),
                new NodeRecord("type:ConsumerB", "class", "ConsumerB", "App.ConsumerB", "src/ConsumerB.cs"),
            ],
            CancellationToken.None);
        // The hub is depended on by two consumers (degree 2); the leaf has no edges (degree 0).
        await _store.UpsertEdgesAsync(
            [
                new SemanticEdgeRecord("type:ConsumerA", "type:Hub", "di_injects", 0.9, 1.0),
                new SemanticEdgeRecord("type:ConsumerB", "type:Hub", "di_injects", 0.9, 1.0),
            ],
            CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        var directory = Path.GetDirectoryName(_databasePath);
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_databasePath}"));
        try
        {
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
