using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// GetContentHashesAsync backs retrieval-time content dedup: it maps requested paths to their stored hashes and
// omits unknown paths.
public sealed class WorkspaceIndexContentHashTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-tests", Guid.NewGuid().ToString("N"), "fuse.db");

    [Fact]
    public async Task ReturnsHashesForKnownPathsAndOmitsUnknown()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);
        await store.UpsertFilesAsync(
        [
            new IndexedFileRecord("a/A.cs", "a/A.cs", ".cs", 10, 0, "hash-a"),
            new IndexedFileRecord("b/B.cs", "b/B.cs", ".cs", 10, 0, "hash-b"),
        ], CancellationToken.None);

        var hashes = await store.GetContentHashesAsync(["a/A.cs", "b/B.cs", "c/Missing.cs"], CancellationToken.None);

        Assert.Equal("hash-a", hashes["a/A.cs"]);
        Assert.Equal("hash-b", hashes["b/B.cs"]);
        Assert.False(hashes.ContainsKey("c/Missing.cs"));
    }

    [Fact]
    public async Task EmptyRequestReturnsEmpty()
    {
        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        var hashes = await store.GetContentHashesAsync([], CancellationToken.None);

        Assert.Empty(hashes);
    }

    public void Dispose()
    {
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
