using Fuse.Cli.Mcp;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Microsoft.Data.Sqlite;

namespace Fuse.Cli.Tests.Mcp;

// R21: corrupt or version-mismatched fuse.db self-heals and surfaces index_rebuilding: to MCP callers.
public sealed class IndexSelfHealTests : IDisposable
{
    private readonly string _root;
    private readonly string _databasePath;

    public IndexSelfHealTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "fuse-self-heal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        _databasePath = FuseStorePaths.ResolveDatabasePath(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
    }

    [Fact]
    public async Task CorruptDatabase_RecreatesSchema_OnInitialize()
    {
        await File.WriteAllTextAsync(_databasePath, "not a sqlite database");

        await using var store = new WorkspaceIndexStore(_databasePath);
        var outcome = await store.InitializeAsync(CancellationToken.None);

        Assert.True(outcome.RebuiltEmptyStore);
        Assert.Contains("corrupt", outcome.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VersionMismatch_ReturnsIndexRebuildingPrefix_FromCoordinator()
    {
        await SeedPopulatedAsync(stampedVersion: "999999.0.0");

        var ex = await Assert.ThrowsAsync<IndexRebuildingException>(() =>
            IndexCoordinator.Default.OpenForReadOnlyAsync(_root, CancellationToken.None));

        var message = FuseOperationalErrors.FromException(ex);
        Assert.StartsWith(FuseOperationalErrors.IndexRebuildingPrefix, message);
        Assert.Contains("after upgrade to", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptDatabase_SubsequentInitialize_SucceedsAfterRecovery()
    {
        await File.WriteAllTextAsync(_databasePath, "not a sqlite database");

        await using (var first = new WorkspaceIndexStore(_databasePath))
        {
            var outcome = await first.InitializeAsync(CancellationToken.None);
            Assert.True(outcome.RebuiltEmptyStore);
        }

        await using var second = new WorkspaceIndexStore(_databasePath);
        var secondOutcome = await second.InitializeAsync(CancellationToken.None);
        Assert.False(secondOutcome.RebuiltEmptyStore);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await second.OpenForReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentRecovery_OnCorruptDatabase_IsSerialized()
    {
        await File.WriteAllTextAsync(_databasePath, "not a sqlite database");

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                await using var store = new WorkspaceIndexStore(_databasePath);
                return await store.InitializeAsync(CancellationToken.None);
            }))
            .ToArray();

        var outcomes = await Task.WhenAll(tasks);
        Assert.Contains(outcomes, o => o.RebuiltEmptyStore);
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await new WorkspaceIndexStore(_databasePath).OpenForReadAsync(CancellationToken.None));
    }

    private async Task SeedPopulatedAsync(string? stampedVersion = null)
    {
        await using (var seed = new WorkspaceIndexStore(_databasePath))
        {
            await seed.InitializeAsync(CancellationToken.None);
            await seed.SetMetaAsync("marker", "keep", CancellationToken.None);
            if (stampedVersion is not null)
                await seed.SetMetaAsync(WorkspaceIndexStore.FuseVersionMetaKey, stampedVersion, CancellationToken.None);
        }

        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={_databasePath}"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
