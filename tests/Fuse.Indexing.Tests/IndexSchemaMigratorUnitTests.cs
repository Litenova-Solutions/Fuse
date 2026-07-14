using Fuse.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fuse.Indexing.Tests;

public sealed class IndexSchemaMigratorUnitTests
{
    [Fact]
    public async Task ReadVersionAsync_returns_zero_before_first_migration()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS schema_version(version INTEGER NOT NULL);";
        await command.ExecuteNonQueryAsync(CancellationToken.None);

        var version = await IndexSchemaMigrator.ReadVersionAsync(connection, CancellationToken.None);

        Assert.Equal(0, version);
    }
}
