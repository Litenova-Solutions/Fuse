using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// R21: corrupt fuse.db is derived data and is deleted and recreated on initialize.
public sealed class WorkspaceIndexCorruptRecoveryTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-tests", Guid.NewGuid().ToString("N"), "fuse.db");

    [Fact]
    public async Task CorruptFile_RecreatesEmptySchema_OnInitialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await File.WriteAllTextAsync(_databasePath, "not a sqlite database");

        await using var store = new WorkspaceIndexStore(_databasePath);
        var outcome = await store.InitializeAsync(CancellationToken.None);

        Assert.True(outcome.RebuiltEmptyStore);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
        var state = await store.GetStateAsync(CancellationToken.None);
        Assert.Equal(0, state.FileCount);
    }

    [Fact]
    public async Task OpenForReadAsync_OnCorruptFile_ReturnsSchemaMismatch()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await File.WriteAllTextAsync(_databasePath, "not a sqlite database");
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_databasePath}"));

        await using var store = new WorkspaceIndexStore(_databasePath);
        var status = await store.OpenForReadAsync(CancellationToken.None);

        Assert.Equal(WorkspaceIndexReadOpenStatus.SchemaMismatch, status);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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
