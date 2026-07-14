using Fuse.Reduction.Caching;
using Microsoft.Data.Sqlite;

namespace Fuse.Fusion.Tests;

internal static class SqliteTestHelpers
{
    internal static string NewDatabasePath(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "fuse.db");
    }

    /// <summary>
    ///     Marks <paramref name="directory" /> as a git repository so persistent store paths resolve under
    ///     <c>.fuse/fuse.db</c> instead of the machine-wide <c>~/.fuse</c> directory.
    /// </summary>
    internal static void InitializeGitRepository(string directory) =>
        Directory.CreateDirectory(Path.Combine(directory, ".git"));

    internal static string FuseDatabasePath(string sourceDirectory) =>
        FuseStorePaths.ResolveDatabasePath(sourceDirectory);

    internal static string FuseCacheDatabasePath(string sourceDirectory) =>
        FuseStorePaths.ResolveCacheDatabasePath(sourceDirectory);

    internal static int CountStoreEntries(string databasePath, string store)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM kv WHERE store = $s";
        command.Parameters.AddWithValue("$s", store);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    internal static void InsertStoreEntry(string databasePath, string store, string key, byte[] value)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS kv(" +
            "  store TEXT NOT NULL," +
            "  key TEXT NOT NULL," +
            "  value BLOB NOT NULL," +
            "  PRIMARY KEY(store, key)) WITHOUT ROWID;" +
            "INSERT OR REPLACE INTO kv(store, key, value) VALUES ($s, $k, $v);";
        command.Parameters.AddWithValue("$s", store);
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        command.ExecuteNonQuery();
    }

    internal static bool StoreEntryEqualsBytes(string databasePath, string store, string key, byte[] expected)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM kv WHERE store = $s AND key = $k";
        command.Parameters.AddWithValue("$s", store);
        command.Parameters.AddWithValue("$k", key);
        var actual = command.ExecuteScalar() as byte[];
        return actual is not null && actual.SequenceEqual(expected);
    }
}
