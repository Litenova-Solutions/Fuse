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

    internal static int CountStoreEntries(string databasePath, string store)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM kv WHERE store = $s";
        command.Parameters.AddWithValue("$s", store);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
