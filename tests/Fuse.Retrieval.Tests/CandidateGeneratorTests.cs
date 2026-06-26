using Fuse.Indexing;
using Fuse.Retrieval;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Retrieval.Tests;

// P5.1: candidate generation per source (exact, FTS, path, diff).
public sealed class CandidateGeneratorTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-candidate-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
        await SeedAsync();
    }

    [Fact]
    public async Task ExactServiceGeneratesServiceExactCandidate()
    {
        var candidates = await Generate(new LocalizationRequest(".", Service: "IOrderService"));

        Assert.Contains(candidates, c =>
            c.Source == CandidateSource.ServiceExact
            && c.FilePath == "src/OrderService.cs"
            && c.BaseScore == 0.95);
    }

    [Fact]
    public async Task FtsQueryGeneratesFtsCandidate()
    {
        var candidates = await Generate(new LocalizationRequest(".", Query: "OrderService"));

        Assert.Contains(candidates, c =>
            (c.Source == CandidateSource.FtsSymbol || c.Source == CandidateSource.FtsBody)
            && c.FilePath == "src/OrderService.cs");
    }

    [Fact]
    public async Task PathQueryGeneratesPathCandidate()
    {
        var candidates = await Generate(new LocalizationRequest(".", Query: "OrderService"));

        Assert.Contains(candidates, c =>
            c.Source == CandidateSource.FtsPath && c.FilePath == "src/OrderService.cs");
    }

    [Fact]
    public async Task SelectedPathsGenerateDiffCandidates()
    {
        var candidates = await Generate(new LocalizationRequest(".", SelectedPaths: ["src/OrderService.cs"]));

        Assert.Contains(candidates, c =>
            c.Source == CandidateSource.DiffChangedFile
            && c.FilePath == "src/OrderService.cs"
            && c.BaseScore == 1.00);
    }

    [Fact]
    public async Task NoSignalYieldsNoCandidates()
    {
        var candidates = await Generate(new LocalizationRequest("."));

        Assert.Empty(candidates);
    }

    private async Task<IReadOnlyList<CandidateNode>> Generate(LocalizationRequest request) =>
        await CandidateGenerator.CreateDefault(_store).GenerateAsync(request, CancellationToken.None);

    private async Task SeedAsync()
    {
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/IOrderService.cs", "src/IOrderService.cs", ".cs", 10, 1, "h1"),
                new IndexedFileRecord("src/OrderService.cs", "src/OrderService.cs", ".cs", 20, 1, "h2"),
            ],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:OrderService", "src/OrderService.cs", "type", "k", 1, 20, "th", 50, 20,
                    Name: "OrderService", Body: "the order service implementation"),
            ],
            CancellationToken.None);
        await _store.UpsertNodesAsync(
            [
                new NodeRecord("type:App.IOrderService", "interface", "IOrderService", "App.IOrderService", "src/IOrderService.cs"),
                new NodeRecord("type:App.OrderService", "class", "OrderService", "App.OrderService", "src/OrderService.cs"),
            ],
            CancellationToken.None);
        await _store.UpsertEdgesAsync(
            [new SemanticEdgeRecord("type:App.IOrderService", "type:App.OrderService", "di_resolves_to", 0.95, 0.95)],
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
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
