using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

// The version-drift self-heal (R22): index reuse is gated on the extraction-contract version, not the product
// version. An index built under a different extraction contract is rebuilt on the next init; a differing
// product version alone (same extraction contract) is reused, and a compatible index is left intact.
public sealed class WorkspaceIndexVersionHealTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-index-tests", Guid.NewGuid().ToString("N"), "fuse.db");

    [Fact]
    public async Task IncompatibleStampTriggersRebuild()
    {
        // R22: an older extraction-contract stamp forces a rebuild (a differing product version alone does not).
        await SeedAsync(markerValue: "keep", stampedExtractionVersion: "0");

        await using var store = new WorkspaceIndexStore(_databasePath);
        var outcome = await store.InitializeAsync(CancellationToken.None);

        // The rebuild drops and recreates index_meta, so the marker and the stale stamp are gone.
        Assert.True(outcome.RebuiltEmptyStore);
        Assert.Contains("extraction contract", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(await store.GetMetaAsync("marker", CancellationToken.None));
    }

    [Fact]
    public async Task DifferentProductVersion_SameExtractionContract_IsReused()
    {
        // R22: a differing product version with the current extraction contract reuses the index (no rebuild).
        await SeedAsync(markerValue: "keep", stampedVersion: "999999.0.0");

        await using var store = new WorkspaceIndexStore(_databasePath);
        var outcome = await store.InitializeAsync(CancellationToken.None);

        Assert.False(outcome.RebuiltEmptyStore);
        Assert.Equal("keep", await store.GetMetaAsync("marker", CancellationToken.None));
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

    // Builds an index at the current schema, writes a marker, and optionally stamps a fuse_version or an
    // extraction-contract version, then closes the pool so the next store opens a clean handle.
    private async Task SeedAsync(string markerValue, string? stampedVersion = null, string? stampedExtractionVersion = null)
    {
        await using (var seed = new WorkspaceIndexStore(_databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.SetMetaAsync("marker", markerValue, CancellationToken.None);
            if (stampedVersion is not null)
                await seed.SetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, stampedVersion, CancellationToken.None);
            if (stampedExtractionVersion is not null)
                await seed.SetMetaAsync(WorkspaceIndexStore.ExtractionVersionMetaKey, stampedExtractionVersion, CancellationToken.None);
        }

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
            // Best-effort cleanup of temp test artifacts.
        }
    }
}
