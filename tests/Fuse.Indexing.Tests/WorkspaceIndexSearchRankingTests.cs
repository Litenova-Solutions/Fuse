using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// N1 (finding 4): the FTS5 bm25 field weights must rank a term hitting a declared symbol name above the same
// term appearing only in a folder path. Before the fix the path column was weighted highest (4.0), inverting the
// documented intent. This test pins the corrected ordering so the class of bug is caught by a recorded test.
public sealed class WorkspaceIndexSearchRankingTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-rank-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceIndexStore(_databasePath);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Symbol_name_match_outranks_path_only_match()
    {
        // Two files. In one, the query term "widget" is the declared symbol name; in the other, "widget" appears
        // only in the folder path while the symbol is unrelated ("Bar").
        await _store.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/core/Widget.cs", "src/core/Widget.cs", ".cs", 100, 1, "h-name"),
                new IndexedFileRecord("src/widget/Bar.cs", "src/widget/Bar.cs", ".cs", 100, 1, "h-path"),
            ],
            CancellationToken.None);
        await _store.UpsertChunksAsync(
            [
                new ChunkRecord("chunk:Widget", "src/core/Widget.cs", "type", "App.Core.Widget", 1, 20, "t1", 40, 20,
                    SymbolId: "symbol:App.Core.Widget", Name: "Widget", SymbolsText: "Widget", Body: "public sealed class Widget { }"),
                new ChunkRecord("chunk:Bar", "src/widget/Bar.cs", "type", "App.Widget.Bar", 1, 20, "t2", 40, 20,
                    SymbolId: "symbol:App.Widget.Bar", Name: "Bar", SymbolsText: "Bar", Body: "public sealed class Bar { }"),
            ],
            CancellationToken.None);

        var hits = await _store.SearchAsync(new SearchQuery("widget", 10), CancellationToken.None);

        Assert.NotEmpty(hits);
        // Both chunks match "widget" (one on name+symbols, one on path). The symbol-name chunk must rank first.
        var top = hits[0];
        Assert.Equal("src/core/Widget.cs", top.FilePath);
        var nameHit = hits.Single(h => h.FilePath == "src/core/Widget.cs");
        var pathHit = hits.SingleOrDefault(h => h.FilePath == "src/widget/Bar.cs");
        if (pathHit is not null)
            Assert.True(nameHit.Score > pathHit.Score,
                $"symbol-name hit ({nameHit.Score}) must outrank path-only hit ({pathHit.Score})");
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        var directory = Path.GetDirectoryName(_databasePath);
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
