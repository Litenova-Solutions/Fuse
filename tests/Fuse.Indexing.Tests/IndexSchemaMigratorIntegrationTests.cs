using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

public sealed class IndexSchemaMigratorIntegrationTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), "fuse-schema-port-tests", Guid.NewGuid().ToString("N"), "fuse.db");
    private WorkspaceIndexConnectionFactory? _factory;

    [Fact]
    public async Task MigrateAsync_creates_schema_at_target_version()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _factory = new WorkspaceIndexConnectionFactory(_databasePath);
        var migrator = new IndexSchemaMigrator(_factory);

        await using var connection = await _factory.OpenAsync(CancellationToken.None);
        await migrator.PrepareDatabaseAsync(connection, CancellationToken.None);
        var version = await IndexSchemaMigrator.MigrateAsync(connection, CancellationToken.None);
        await IndexSchemaMigrator.EnsureTablesAsync(connection, CancellationToken.None);

        Assert.Equal(WorkspaceIndexSchema.TargetVersion, version);
        Assert.Equal(WorkspaceIndexSchema.TargetVersion, await IndexSchemaMigrator.ReadVersionAsync(connection, CancellationToken.None));
    }

    [Fact]
    public async Task WriteMetaAsync_round_trips_through_read()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _factory = new WorkspaceIndexConnectionFactory(_databasePath);

        await using var connection = await _factory.OpenAsync(CancellationToken.None);
        await new IndexSchemaMigrator(_factory).PrepareDatabaseAsync(connection, CancellationToken.None);
        await IndexSchemaMigrator.MigrateAsync(connection, CancellationToken.None);
        await IndexSchemaMigrator.EnsureTablesAsync(connection, CancellationToken.None);
        await IndexSchemaMigrator.WriteMetaAsync(connection, "probe", "value", CancellationToken.None);

        Assert.Equal("value", await IndexSchemaMigrator.ReadMetaAsync(connection, "probe", CancellationToken.None));
    }

    public void Dispose() => _factory?.ClearPool();
}
