using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// The version-drift self-heal: an index built by an incompatible Fuse (different major.minor) is rebuilt on the
// next init; a compatible or unstamped index is left intact.
public sealed class WorkspaceIndexVersionHealTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-tests", Guid.NewGuid().ToString("N"), "fuse.db");

    [Fact]
    public async Task IncompatibleStampTriggersRebuild()
    {
        await SeedAsync(markerValue: "keep", stampedVersion: "999999.0.0");

        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        // The rebuild drops and recreates index_meta, so the marker and the stale stamp are gone.
        Assert.Null(await store.GetMetaAsync("marker", CancellationToken.None));
        Assert.Null(await store.GetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, CancellationToken.None));
    }

    [Fact]
    public async Task CompatibleStampIsPreserved()
    {
        await SeedAsync(markerValue: "keep", stampedVersion: FuseBuildInfo.Current);

        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        Assert.Equal("keep", await store.GetMetaAsync("marker", CancellationToken.None));
    }

    [Fact]
    public async Task UnstampedIndexIsNotWiped()
    {
        await SeedAsync(markerValue: "keep", stampedVersion: null);

        await using var store = new WorkspaceIndexStore(_databasePath);
        await store.InitializeAsync(CancellationToken.None);

        Assert.Equal("keep", await store.GetMetaAsync("marker", CancellationToken.None));
    }

    // Builds an index at the current schema, writes a marker, and optionally stamps a fuse_version, then closes
    // the pool so the next store opens a clean handle.
    private async Task SeedAsync(string markerValue, string? stampedVersion)
    {
        await using (var seed = new WorkspaceIndexStore(_databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.SetMetaAsync("marker", markerValue, CancellationToken.None);
            if (stampedVersion is not null)
                await seed.SetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, stampedVersion, CancellationToken.None);
        }

        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_databasePath}"));
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
