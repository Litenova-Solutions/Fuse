using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// R22: index freshness is gated on the extraction-contract version and the schema version, never the product
// version. A minor/patch product bump reuses a good index; only an extraction-contract or schema change rebuilds.
public sealed class ExtractionContractVersionTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-r22", Guid.NewGuid().ToString("N"), "fuse.db");

    [Fact]
    public async Task DifferentFuseVersion_SameExtractionAndSchema_ReusesIndex_NoRebuild()
    {
        await SeedAsync(stampFuseVersion: "999999.0.0"); // a wildly different product version.

        await using var store = new WorkspaceIndexStore(_databasePath);
        var outcome = await store.InitializeAsync(CancellationToken.None);

        Assert.False(outcome.RebuiltEmptyStore);
        Assert.Equal("keep", await store.GetMetaAsync("marker", CancellationToken.None));
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
        var state = await store.GetStateAsync(CancellationToken.None);
        Assert.True(state.FileCount > 0); // non-empty reads.
    }

    [Fact]
    public async Task OlderExtractionVersion_Rebuilds()
    {
        await SeedAsync(stampExtractionVersion: "0"); // older than the current contract.

        await using var store = new WorkspaceIndexStore(_databasePath);
        var outcome = await store.InitializeAsync(CancellationToken.None);

        Assert.True(outcome.RebuiltEmptyStore);
        Assert.Contains("extraction contract", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.GetMetaAsync("marker", CancellationToken.None)); // derived data was rebuilt away.
    }

    [Fact]
    public async Task PreR22Store_NoExtractionStamp_ButFuseVersionPresent_RebuildsOnce()
    {
        await SeedAsync(stampFuseVersion: "4.1.0");
        // Remove the extraction stamp to simulate a pre-R22 store that only carried fuse_version.
        await ExecuteAsync("DELETE FROM index_meta WHERE key = 'index_extraction_version';");

        await using (var first = new WorkspaceIndexStore(_databasePath))
        {
            var outcome = await first.InitializeAsync(CancellationToken.None);
            Assert.True(outcome.RebuiltEmptyStore); // one-time rebuild to gain the stamp.
        }

        await using var second = new WorkspaceIndexStore(_databasePath);
        var secondOutcome = await second.InitializeAsync(CancellationToken.None);
        Assert.False(secondOutcome.RebuiltEmptyStore); // now stamped: reused.
    }

    [Fact]
    public async Task OlderSchemaVersion_Rebuilds()
    {
        await SeedAsync();
        await ExecuteAsync("DELETE FROM schema_version; INSERT INTO schema_version(version) VALUES(1);");

        await using var store = new WorkspaceIndexStore(_databasePath);
        var outcome = await store.InitializeAsync(CancellationToken.None);

        Assert.True(outcome.RebuiltEmptyStore);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
    }

    private async Task SeedAsync(string? stampFuseVersion = null, string? stampExtractionVersion = null)
    {
        await using (var seed = new WorkspaceIndexStore(_databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.UpsertFilesAsync(
            [
                new IndexedFileRecord("src/Seed.cs", "src/Seed.cs", ".cs", 10, 0, "hash-seed", Language: "csharp"),
            ], CancellationToken.None);
            await seed.SetMetaAsync("marker", "keep", CancellationToken.None);
            if (stampFuseVersion is not null)
                await seed.SetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, stampFuseVersion, CancellationToken.None);
            if (stampExtractionVersion is not null)
                await seed.SetMetaAsync(WorkspaceIndexStore.ExtractionVersionMetaKey, stampExtractionVersion, CancellationToken.None);
        }

    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        SqliteConnection.ClearPool(connection);
    }

    public void Dispose()
    {
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
