using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// F-008: the retired flat per-source FTS generator is diagnostic-only; shipping uses LexicalCandidateGenerator.
public sealed class FtsCandidateGeneratorDiagnosticTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-flat-fts-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await _store.UpsertFilesAsync(
            [new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 20, 1, "h1")],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:OrderService", "src/OrderService.cs", "type", "k", 1, 20, "th1", 50, 20,
                    Name: "OrderService", Body: "the order service implementation"),
            ],
            CancellationToken.None);
    }

    [Fact]
    public void FlatFtsDiagnosticFlagIsOffByDefault() => Assert.False(RetrievalDiagnosticFlags.EnableFlatFts);

    [Fact]
    public async Task FlatFtsDiagnosticAssignsFlatPerSourceWeightWithoutRankDecay()
    {
        var candidates = await new FtsCandidateGenerator(_store)
            .GenerateAsync(new LocalizationRequest(".", Query: "OrderService"), CancellationToken.None);

        var match = Assert.Single(candidates, c => c.FilePath == "src/OrderService.cs");
        Assert.Equal(CandidateSource.FtsSymbol, match.Source);
        Assert.Equal(CandidateSourceWeights.Weight(CandidateSource.FtsSymbol), match.BaseScore);
        Assert.DoesNotContain(match.Reasons, r => r.Contains("PRF", StringComparison.Ordinal));
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
